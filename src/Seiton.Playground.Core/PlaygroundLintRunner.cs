using System.Buffers;
using System.Text;
using System.Text.Json;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;

namespace Seiton.Playground;

/// <summary>
/// Runs <see cref="LintEngine"/> and serializes diagnostics to a JSON array for the playground UI.
/// <para>
/// Reuses a single <see cref="LintEngine"/> instance across calls.
/// <see cref="LintEngine.Check(byte[], string, LintConfig?)"/> clears all internal lists at the top of each call, and each
/// <see cref="Seiton.Core.Linting.RuleBase"/> clears its diagnostics in <c>VisitWorkflowPre</c> /
/// <c>VisitActionMetadataPre</c>, so reuse is safe.
/// Creating a <b>new</b> engine per call would allocate 50+ rule objects every keystroke,
/// enormously increasing GC pressure in the constrained WASM heap.
/// </para>
/// </summary>
public static class PlaygroundLintRunner
{
    /// <summary>
    /// Shared engine. WASM is single-threaded so the lock is uncontended at runtime,
    /// but it is required for correctness when the same static is accessed by parallel
    /// test runners on desktop .NET.
    /// </summary>
    private static readonly LintEngine Engine = new();
    private static readonly object EngineGate = new();

    /// <summary>Apply fixes for higher-priority rule IDs before broader structural autofixes (otherwise one batch can corrupt YAML).</summary>
    private static readonly string[] FixRuleApplyPriority =
    [
        "deny-write-all",
        "checkout-persist-credentials",
    ];

    /// <summary>Lint runs with fixes enabled so diagnostics can expose <see cref="PlaygroundDiagnosticDto.Fixable"/> metadata.</summary>
    private static readonly LintConfig LintWithFixMetadata = new()
    {
        Fix = new FixConfig { Enabled = true },
        Network = new NetworkConfig(),
        Output = new OutputConfig(),
        SkipSuppressionSummary = true,
    };

    /// <summary>Reusable buffer for JSON serialization. Guarded by <see cref="EngineGate"/>.</summary>
    private static readonly ArrayBufferWriter<byte> JsonBuffer = new(4096);

    /// <summary>Cached severity display strings indexed by <see cref="DiagnosticSeverity"/>.</summary>
    private static readonly string[] SeverityStrings = ["Info", "Warning", "Error"];

    private static readonly JsonWriterOptions CamelCaseWriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Incremental parse context for D-5b root section reuse. Guarded by <see cref="EngineGate"/>.</summary>
    private static readonly IncrementalParseContext IncrementalCtx = new();

    // ─── Config Content-Hash Caching ───

    /// <summary>Default config used when no user config is set.</summary>
    private static readonly LintConfig DefaultConfig = LintWithFixMetadata;

    /// <summary>XxHash64 of normalized config content. Zero when no user config is set.</summary>
    private static ulong _configHash;

    /// <summary>Last successfully parsed user config. Null means use <see cref="DefaultConfig"/>.</summary>
    private static LintConfig? _cachedConfig;

    /// <summary>Shared empty JSON array literal <c>[]</c>.</summary>
    private static readonly byte[] EmptyJsonArray = "[]"u8.ToArray();

    /// <summary>Last SetConfig diagnostic result (cached for hash-hit zero-alloc return).</summary>
    private static byte[] _cachedConfigDiag = EmptyJsonArray;

    // ─── Identity-based short circuit ───
    private static string? _lastYamlSource;

    /// <summary>Cached last file path for identity-based short circuit. Guarded by <see cref="EngineGate"/>.</summary>
    private static string? _lastFilePath;

    /// <summary>Cached last JSON output for identity-based short circuit. Guarded by <see cref="EngineGate"/>.</summary>
    private static byte[]? _lastJsonOutput;

    /// <summary>Gets the active lint config (user config if set, otherwise default).</summary>
    private static LintConfig ActiveConfig => _cachedConfig ?? DefaultConfig;

    /// <summary>Clears shared caches and incremental parse state between playground tests.</summary>
    internal static void ResetSharedStateForTests()
    {
        lock (EngineGate)
        {
            _cachedConfig = null;
            _configHash = 0;
            _cachedConfigDiag = EmptyJsonArray;
            InvalidateLintCache();
            IncrementalCtx.ResetForTests();
            ActionShaResolverOverride = null;
            ImageDigestResolverOverride = null;
            ForceUseIncrementalLintForTests = null;
        }
    }

    // ─── Resolver Overrides (internal for testability) ───

    /// <summary>Override for action SHA resolver. Used in tests; production uses <see cref="DefaultActionShaResolver"/>.</summary>
    internal static IActionShaResolver? ActionShaResolverOverride;

    /// <summary>Override for image digest resolver. Used in tests; production uses <see cref="DefaultImageDigestResolver"/>.</summary>
    internal static IImageDigestResolver? ImageDigestResolverOverride;

    /// <summary>
    /// When false, <see cref="RunToJsonUtf8"/> uses full parse+lint (no incremental job cache).
    /// Disabled in the browser: incremental reuse can retain stale spans across edits and trap in WASM AOT.
    /// Must be evaluated per call — <see cref="OperatingSystem.IsBrowser"/> is false during static type init in WASM.
    /// </summary>
    internal static bool? ForceUseIncrementalLintForTests { get; set; }
    private static bool UseIncrementalLint => ForceUseIncrementalLintForTests ?? !OperatingSystem.IsBrowser();

    /// <summary>
    /// Sets the lint configuration from YAML text. Uses content-hash caching (XxHash64) to
    /// skip re-parse when the normalized config content has not meaningfully changed.
    /// </summary>
    /// <param name="configYaml">
    /// Config YAML text (same format as <c>seiton.yaml</c>).
    /// Null, empty, or whitespace-only resets to default config.
    /// </param>
    /// <returns>
    /// UTF-8 JSON byte array of config diagnostics. Empty array <c>[]</c> on success.
    /// On validation errors, returns diagnostic array; previous valid config is retained.
    /// </returns>
    public static byte[] SetConfig(string? configYaml)
    {
        lock (EngineGate)
        {
            // Null/empty/whitespace: reset to default
            if (string.IsNullOrWhiteSpace(configYaml))
            {
                _cachedConfig = null;
                _configHash = 0;
                _cachedConfigDiag = EmptyJsonArray;
                InvalidateLintCache();
                return EmptyJsonArray;
            }

            // Normalize: strip trailing whitespace per line, remove blank lines
            var normalized = NormalizeConfigForHash(configYaml);

            // Compute XxHash64
            var byteCount = Encoding.UTF8.GetByteCount(normalized);
            byte[]? rented = null;
            Span<byte> utf8Span = byteCount <= 1024
                ? stackalloc byte[byteCount]
                : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
            try
            {
                Encoding.UTF8.GetBytes(normalized, utf8Span);
                var hash = XxHash64.Hash(utf8Span);

                // Hash-hit: return cached diagnostics bytes (skips config parse).
                // If hash matches but user config was cleared without resetting the hash, re-parse.
                if (hash == _configHash && _configHash != 0
                    && (_cachedConfig is not null || _cachedConfigDiag.Length > 0))
                {
                    return _cachedConfigDiag;
                }

                // Hash-miss: parse config
                var validation = LintConfigLibrary.Validate(configYaml, ".github/seiton.yaml");

                if (validation.IsValid)
                {
                    // Success: update cache.
                    // The playground always needs Fix.Enabled=true (so rules build DiagnosticFix objects
                    // for "Apply all fixes") and SkipSuppressionSummary=true. Force these regardless of
                    // what the user wrote, since these are playground-intrinsic behaviors.
                    var parsed = validation.Config!;
                    var playgroundConfig = new LintConfig
                    {
                        Utf8Yaml = parsed.Utf8Yaml,
                        FilePath = parsed.FilePath,
                        Rules = parsed.Rules,
                        Exclusions = parsed.Exclusions,
                        Fix = parsed.Fix with { Enabled = true },
                        Network = parsed.Network,
                        Output = parsed.Output,
                        SkipSuppressionSummary = true,
                    };
                    _cachedConfig = playgroundConfig;
                    _configHash = hash;
                    _cachedConfigDiag = EmptyJsonArray;
                    InvalidateLintCache();
                    return EmptyJsonArray;
                }

                // Validation errors: keep previous config, serialize diagnostics
                var diagBytes = SerializeConfigDiagnostics(validation.Diagnostics);
                // Update _configHash so repeated calls with the same invalid content are cache hits
                // (avoids re-parsing the same broken config on every keystroke).
                _configHash = hash;
                _cachedConfigDiag = diagBytes;
                // Config content is unchanged, but drop lint caches so the next RunLint cannot
                // reuse per-job diagnostics produced under a prior config (D-5d).
                InvalidateLintCache();
                return diagBytes;
            }
            finally
            {
                if (rented is not null)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
    }

    /// <summary>
    /// Normalizes config YAML for hash stability: strips trailing whitespace per line,
    /// removes blank lines, joins with \n.
    /// </summary>
    private static string NormalizeConfigForHash(string configYaml)
    {
        var lines = configYaml.Split('\n');
        var sb = new StringBuilder(configYaml.Length);
        var first = true;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd();
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (!first)
            {
                sb.Append('\n');
            }
            sb.Append(trimmed);
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>Serializes config validation diagnostics to UTF-8 JSON.</summary>
    private static byte[] SerializeConfigDiagnostics(Diagnostic[] diagnostics)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, CamelCaseWriterOptions))
        {
            WriteDiagnosticsArray(writer, diagnostics.AsSpan());
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Invalidates identity-based lint cache so next RunToJsonUtf8 re-lints.</summary>
    private static void InvalidateLintCache()
    {
        _lastYamlSource = null;
        _lastFilePath = null;
        _lastJsonOutput = null;
        _defaultActionShaResolver = null;
        _defaultImageDigestResolver = null;
        IncrementalCtx.InvalidateLintDiagnosticCache();
    }

    /// <summary>
    /// Parses and lints <paramref name="yamlSource"/> and returns a UTF-8 JSON byte array of diagnostics.
    /// Uses incremental parsing (D-5b) to skip unchanged root sections when possible.
    /// Suitable for WASM interop where JavaScript can decode with TextDecoder, or for
    /// scenarios where the result is written directly to a stream.
    /// </summary>
    public static byte[] RunToJsonUtf8(string yamlSource, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        lock (EngineGate)
        {
            // Fast path: if source and filePath are identical to last call, return cached output
            if (ReferenceEquals(yamlSource, _lastYamlSource)
                && string.Equals(filePath, _lastFilePath, StringComparison.Ordinal)
                && _lastJsonOutput is not null)
            {
                return _lastJsonOutput;
            }

            var utf8Yaml = EncodeToDoubleBuffer(yamlSource);

            byte[] result;

            var config = ActiveConfig;

            // Lifetime invariant:
            // DiagnosticList/Diagnostic spans can reference arena-owned storage.
            // Never let a diagnostic span outlive the owning LintResult/AstArena.
            // Always serialize (or copy) diagnostics before disposing those owners.
            // Browser path: `using var lintResult = Engine.Check(...)` then serialize inside the block.

            // Action metadata files (action.yml) require classified parsing — not incremental.
            if (DocumentKindClassifier.GetPathHintKind(filePath) == DocumentKind.ActionMetadata)
            {
                result = LintActionMetadataToJsonUtf8(utf8Yaml, filePath, config);
            }
            else if (UseIncrementalLint)
            {
                // D-5b/5c: Use incremental parse to skip unchanged root sections and jobs
                using var parseResult = IncrementalCtx.ParseIncrementally(utf8Yaml, filePath);

                // D-5d: Build skip mask — reused jobs with cached diagnostics skip lint
                var jobCount = parseResult.Workflow?.Jobs.Count ?? 0;
                var skipJobs = IncrementalCtx.BuildSkipJobs(jobCount, parseResult.Workflow);

                // Lint with optional job skipping
                var lintResultData = Engine.CheckWithParseResult(utf8Yaml, filePath, config, parseResult.Data, IncrementalCtx.Arena, skipJobs);

                // Merge fresh diagnostics with cached diagnostics for skipped jobs
                result = SerializeDiagnosticsToResult(IncrementalCtx.MergeDiagnosticsWithCache(lintResultData.Diagnostics, skipJobs));
            }
            else
            {
                using var lintResult = Engine.Check(utf8Yaml, filePath, config);
                result = SerializeDiagnosticsToResult(lintResult.Diagnostics.AsSpan());
            }

            // Cache for identity-based short circuit
            _lastYamlSource = yamlSource;
            _lastFilePath = filePath;
            _lastJsonOutput = result;

            return result;
        }
    }

    /// <summary>
    /// Lints action metadata with a caller-owned arena. Serializes diagnostics before arena disposal.
    /// </summary>
    private static byte[] LintActionMetadataToJsonUtf8(byte[] utf8Yaml, string filePath, LintConfig config)
    {
        var classifiedResult = WorkflowParser.ParseClassified(utf8Yaml, filePath, out var arena);
        try
        {
            var lintResultData = Engine.CheckWithParseResult(utf8Yaml, filePath, config, classifiedResult.ParseResult, arena);
            return SerializeDiagnosticsToResult(lintResultData.Diagnostics.AsSpan());
        }
        finally
        {
            arena?.Dispose();
        }
    }

    private static byte[] SerializeDiagnosticsToResult(ReadOnlySpan<Diagnostic> diagnostics)
    {
        JsonBuffer.Clear();
        using (var writer = new Utf8JsonWriter(JsonBuffer, CamelCaseWriterOptions))
        {
            WriteDiagnosticsArray(writer, diagnostics);
        }

        var written = JsonBuffer.WrittenSpan;
        if (_lastJsonOutput is not null && written.SequenceEqual(_lastJsonOutput))
        {
            return _lastJsonOutput;
        }

        return written.ToArray();
    }

    private static void WriteDiagnosticsArray(Utf8JsonWriter writer, ReadOnlySpan<Diagnostic> diagnostics)
    {
        writer.WriteStartArray();
        for (var i = 0; i < diagnostics.Length; i++)
        {
            var d = diagnostics[i];
            var loc = d.Location;

            writer.WriteStartObject();
            writer.WriteString("message", d.Message);
            writer.WriteNumber("line", loc.StartLine);
            writer.WriteNumber("column", loc.StartColumn);
            writer.WriteString("severity", SeverityString(d.Severity));
            if (d.RuleId is not null)
                writer.WriteString("ruleId", d.RuleId);
            else
                writer.WriteNull("ruleId");
            writer.WriteBoolean("fixable", d.Fix is not null);
            if (d.Fix?.Description is { } fixDesc)
                writer.WriteString("fixDescription", fixDesc);
            else
                writer.WriteNull("fixDescription");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static string SeverityString(DiagnosticSeverity severity)
    {
        var index = (int)severity;
        return (uint)index < (uint)SeverityStrings.Length ? SeverityStrings[index] : severity.ToString();
    }

    /// <summary>
    /// Applies autofix-aware diagnostics sequentially (rule preference order below) until none remain or a safety iteration cap is hit.
    /// </summary>
    public static string ApplyAllFixes(string yamlSource, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var current = Encoding.UTF8.GetBytes(yamlSource);
        const int maxPasses = 64;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            lock (EngineGate)
            {
                using var result = Engine.Check(current, filePath, ActiveConfig);
                if (!result.HasFixableDiagnostics)
                {
                    return Encoding.UTF8.GetString(current);
                }

                var filtered = CollectAutoApplicableFixes(result.FixableDiagnostics);
                if (filtered.Length == 0)
                {
                    // Still has diagnostics with fixes attached, but none we auto-apply here (see CollectAutoApplicableFixes).
                    return Encoding.UTF8.GetString(current);
                }

                var diag = PickNextDiagnosticToApply(filtered);
                current = FixEngine.Apply(current, new[] { diag });
            }
        }

        return Encoding.UTF8.GetString(current);
    }

    /// <summary>
    /// Applies all fixes including network-based pin remediation (SHA/digest resolution).
    /// First applies offline fixes (synchronously), then resolves and applies pin fixes via network.
    /// </summary>
    public static async Task<AsyncFixResult> ApplyAllFixesAsync(
        string yamlSource, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        // Apply all offline fixes first (same as sync path)
        var afterOffline = ApplyAllFixes(yamlSource, filePath);

        // Network-based pin remediation
        LintConfig config;
        bool pinningEnabled;
        bool imagesEnabled;
        lock (EngineGate)
        {
            config = ActiveConfig;
            pinningEnabled = config.Fix.Pinning.EnableNetwork;
            imagesEnabled = config.Fix.Images.EnableNetwork;
        }

        if (!pinningEnabled && !imagesEnabled)
        {
            return new AsyncFixResult(afterOffline, ResolvedCount: 0, SkippedCount: 0, FailedCount: 0);
        }

        // Lint the offline-fixed YAML to get remaining diagnostics (including unpinned-uses/unpinned-image)
        var utf8Yaml = Encoding.UTF8.GetBytes(afterOffline);
        Diagnostic[] diagnostics;
        lock (EngineGate)
        {
            using var lintResult = Engine.Check(utf8Yaml, filePath, config);
            diagnostics = lintResult.Diagnostics.ToArray();
        }

        // Filter to only pin-eligible diagnostics for the enabled resolver(s).
        // Only include diagnostics whose resolver is actually enabled — avoids inflating
        // SkippedCount with diagnostics for a feature the user didn't enable.
        var pinDiagnostics = FilterPinEligibleDiagnostics(diagnostics, pinningEnabled, imagesEnabled);

        if (pinDiagnostics.Length == 0)
        {
            return new AsyncFixResult(afterOffline, ResolvedCount: 0, SkippedCount: 0, FailedCount: 0);
        }

        // Create PinRemediationEngine with resolvers
        var actionResolver = ActionShaResolverOverride ?? DefaultActionShaResolver;
        var imageResolver = ImageDigestResolverOverride ?? DefaultImageDigestResolver;

        var engine = new PinRemediationEngine(
            pinningEnabled ? actionResolver : null,
            imagesEnabled ? imageResolver : null,
            config.Fix.Pinning,
            config.Fix.Images,
            config.Network);

        // Remediate: resolve SHAs/digests and attach fixes
        var remediationResult = await engine.RemediateAsync(pinDiagnostics, utf8Yaml, cancellationToken);

        // Apply all pin fixes in a single batch pass. All offsets reference the same
        // utf8Yaml source, so applying them one-by-one would shift offsets and corrupt later edits.
        var current = utf8Yaml;
        var fixablePinDiags = CollectFixableDiagnostics(remediationResult.Diagnostics);

        if (fixablePinDiags.Length > 0)
        {
            current = FixEngine.Apply(current, fixablePinDiags);
        }

        var finalYaml = Encoding.UTF8.GetString(current);
        return new AsyncFixResult(
            finalYaml,
            remediationResult.ResolvedCount,
            remediationResult.SkippedCount,
            remediationResult.FailedCount);
    }

    // ─── Default Resolvers (lazy-initialized, long-lived for caching) ───

    private static HttpClient? _githubHttpClient;
    private static GitHubActionShaResolver? _defaultActionShaResolver;

    private static IActionShaResolver? DefaultActionShaResolver
    {
        get
        {
            if (_defaultActionShaResolver is not null)
                return _defaultActionShaResolver;

            lock (EngineGate)
            {
                if (_defaultActionShaResolver is not null)
                    return _defaultActionShaResolver;

                _githubHttpClient ??= CreatePlaygroundHttpClient("api.github.com");
                var cfg = ActiveConfig;
                _defaultActionShaResolver = new GitHubActionShaResolver(
                    _githubHttpClient,
                    cfg.Fix.Pinning,
                    cfg.Network.GitHub);
                return _defaultActionShaResolver;
            }
        }
    }

    private static HttpClient? _ociHttpClient;
    private static OciImageDigestResolver? _defaultImageDigestResolver;

    private static IImageDigestResolver? DefaultImageDigestResolver
    {
        get
        {
            if (_defaultImageDigestResolver is not null)
                return _defaultImageDigestResolver;

            lock (EngineGate)
            {
                if (_defaultImageDigestResolver is not null)
                    return _defaultImageDigestResolver;

                _ociHttpClient ??= CreatePlaygroundHttpClient(null);
                var cfg = ActiveConfig;
                _defaultImageDigestResolver = new OciImageDigestResolver(
                    _ociHttpClient,
                    cfg.Fix.Images);
                return _defaultImageDigestResolver;
            }
        }
    }

    /// <summary>
    /// Creates an HttpClient suitable for WASM (no SocketsHttpHandler — browser handles CORS/redirects).
    /// </summary>
    private static HttpClient CreatePlaygroundHttpClient(string? baseAddress)
    {
        var client = new HttpClient();
        if (baseAddress is not null)
        {
            client.BaseAddress = new Uri($"https://{baseAddress}");
        }
        client.DefaultRequestHeaders.UserAgent.ParseAdd("seiton-playground/1.0");
        client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        return client;
    }

    private static Diagnostic[] CollectAutoApplicableFixes(Diagnostic[] fixables)
    {
        if (fixables.Length == 0)
        {
            return [];
        }

        var list = new List<Diagnostic>(fixables.Length);
        for (var i = 0; i < fixables.Length; i++)
        {
            var d = fixables[i];
            if (d.RuleId == "deny-read-all")
            {
                continue;
            }

            list.Add(d);
        }

        return list.Count == 0 ? [] : list.ToArray();
    }

    /// <summary>
    /// Filters diagnostics to only pin-eligible ones based on which resolvers are enabled.
    /// Avoids LINQ allocation by using a loop with pre-sized list.
    /// </summary>
    private static Diagnostic[] FilterPinEligibleDiagnostics(Diagnostic[] diagnostics, bool pinningEnabled, bool imagesEnabled)
    {
        var list = new List<Diagnostic>(diagnostics.Length);
        for (var i = 0; i < diagnostics.Length; i++)
        {
            var d = diagnostics[i];
            if (pinningEnabled && d.RuleId == "unpinned-uses")
            {
                list.Add(d);
            }
            else if (imagesEnabled && d.RuleId == "unpinned-image")
            {
                list.Add(d);
            }
        }
        return list.Count == 0 ? [] : list.ToArray();
    }

    /// <summary>
    /// Collects diagnostics that have a fix attached (post-remediation).
    /// Avoids LINQ allocation by using a loop.
    /// </summary>
    private static Diagnostic[] CollectFixableDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        var list = new List<Diagnostic>(diagnostics.Count);
        for (var i = 0; i < diagnostics.Count; i++)
        {
            if (diagnostics[i].Fix is not null)
            {
                list.Add(diagnostics[i]);
            }
        }
        return list.Count == 0 ? [] : list.ToArray();
    }

    private static Diagnostic PickNextDiagnosticToApply(Diagnostic[] fixables)
    {
        for (var p = 0; p < FixRuleApplyPriority.Length; p++)
        {
            var want = FixRuleApplyPriority[p];
            for (var i = 0; i < fixables.Length; i++)
            {
                if (fixables[i].RuleId == want)
                {
                    return fixables[i];
                }
            }
        }

        return fixables[0];
    }

    /// <summary>
    /// Double-buffer for UTF-8 encoding. By alternating between two buffers,
    /// the buffer that <see cref="IncrementalParseContext"/> stores as <c>_previousSource</c>
    /// is never overwritten by the next call. Only allocates when the required exact size
    /// differs from the existing buffer.
    /// Guarded by <see cref="EngineGate"/>.
    /// </summary>
    private static byte[]? _utf8BufA;
    private static byte[]? _utf8BufB;
    private static bool _useUtf8BufA = true;

    /// <summary>
    /// Encodes <paramref name="source"/> into the inactive double-buffer and returns it.
    /// Only allocates when the byte length changes from the last call to this buffer slot.
    /// The returned array has <c>Length == byteCount</c> (exact size) so VYaml and the
    /// parser see no trailing garbage. Must be called under <see cref="EngineGate"/>.
    /// </summary>
    private static byte[] EncodeToDoubleBuffer(string source)
    {
        var byteCount = Encoding.UTF8.GetByteCount(source);
        ref var buf = ref (_useUtf8BufA ? ref _utf8BufA : ref _utf8BufB);
        if (buf is null || buf.Length != byteCount)
        {
            buf = new byte[byteCount];
        }
        Encoding.UTF8.GetBytes(source, buf);
        _useUtf8BufA = !_useUtf8BufA;
        return buf;
    }
}

/// <summary>Result of async fix application including network-based pin remediation.</summary>
public readonly record struct AsyncFixResult(string Yaml, int ResolvedCount, int SkippedCount, int FailedCount);
