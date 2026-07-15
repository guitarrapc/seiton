using System.Buffers;
using System.Text;
using System.Text.Json;
using Seiton.Core.Flow;
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
    private const int MaxRetainedBufferBytes = 256 * 1024;

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
    private static ArrayBufferWriter<byte> JsonBuffer = new(4096);

    /// <summary>Reusable combined lint/flow response buffer. Guarded by <see cref="EngineGate"/>.</summary>
    private static ArrayBufferWriter<byte> LintFlowJsonBuffer = new(4096);

    /// <summary>Reusable Mermaid scratch buffer for combined lint/Mermaid responses. Guarded by <see cref="EngineGate"/>.</summary>
    private static ArrayBufferWriter<byte> MermaidBuffer = new(4096);

    /// <summary>Cached severity display strings indexed by <see cref="DiagnosticSeverity"/>.</summary>
    private static readonly string[] SeverityStrings = ["Info", "Warning", "Error"];

    private static readonly JsonWriterOptions CamelCaseWriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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

    // ─── Content-hash lint caching ───
    /// <summary>Content hash of the last linted YAML (XxHash64). Guarded by <see cref="EngineGate"/>.</summary>
    private static ulong _lastYamlHash;

    /// <summary>Cached last file path for content-hash short circuit. Guarded by <see cref="EngineGate"/>.</summary>
    private static string? _lastFilePath;

    /// <summary>Cached last JSON output for content-hash short circuit. Guarded by <see cref="EngineGate"/>.</summary>
    private static byte[]? _lastJsonOutput;

    /// <summary>Reusable diagnostics JSON buffer returned from <see cref="SerializeDiagnosticsToResult"/>.</summary>
    private static byte[] _diagnosticsJsonBuf = EmptyJsonArray;

    /// <summary>Reusable combined Flow/Mermaid response bytes, bounded to avoid retaining hostile input.</summary>
    private static byte[] _lintFlowJsonOutput = EmptyJsonArray;

    /// <summary>Caller-owned UTF-8 source buffer. Guarded by <see cref="EngineGate"/>.</summary>
    private static byte[] _utf8Yaml = [];

    /// <summary>Gets the active lint config (user config if set, otherwise default).</summary>
    private static LintConfig ActiveConfig => _cachedConfig ?? DefaultConfig;

    /// <summary>Clears shared caches between playground tests.</summary>
    internal static void ResetSharedStateForTests()
    {
        lock (EngineGate)
        {
            _cachedConfig = null;
            _configHash = 0;
            _cachedConfigDiag = EmptyJsonArray;
            _diagnosticsJsonBuf = EmptyJsonArray;
            _lintFlowJsonOutput = EmptyJsonArray;
            JsonBuffer = new ArrayBufferWriter<byte>(4096);
            LintFlowJsonBuffer = new ArrayBufferWriter<byte>(4096);
            MermaidBuffer = new ArrayBufferWriter<byte>(4096);
            _utf8Yaml = [];
            InvalidateLintCache();
            ActionShaResolverOverride = null;
            ImageDigestResolverOverride = null;
        }
    }

    // ─── Resolver Overrides (internal for testability) ───

    /// <summary>Override for action SHA resolver. Used in tests; production uses <see cref="DefaultActionShaResolver"/>.</summary>
    internal static IActionShaResolver? ActionShaResolverOverride;

    /// <summary>Override for image digest resolver. Used in tests; production uses <see cref="DefaultImageDigestResolver"/>.</summary>
    internal static IImageDigestResolver? ImageDigestResolverOverride;

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
                // Config content is unchanged, but drop the lint output cache so the next
                // RunLint cannot reuse output produced under a prior config.
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

    /// <summary>Invalidates content-hash lint cache so next RunToJsonUtf8 re-lints.</summary>
    private static void InvalidateLintCache()
    {
        _lastYamlHash = 0;
        _lastFilePath = null;
        _lastJsonOutput = null;
        _defaultActionShaResolver = null;
        _defaultImageDigestResolver = null;
    }

    /// <summary>
    /// Parses and lints <paramref name="yamlSource"/> and returns a UTF-8 JSON byte array of diagnostics.
    /// Suitable for WASM interop where JavaScript can decode with TextDecoder, or for
    /// scenarios where the result is written directly to a stream.
    /// </summary>
    public static byte[] RunToJsonUtf8(string yamlSource, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        lock (EngineGate)
        {
            var (utf8Yaml, yamlHash) = PlaygroundUtf8Scratch.EncodeAndHash(yamlSource, ref _utf8Yaml);
            if (yamlHash == _lastYamlHash
                && string.Equals(filePath, _lastFilePath, StringComparison.Ordinal)
                && _lastJsonOutput is not null)
            {
                return _lastJsonOutput;
            }

            byte[] result;

            var config = ActiveConfig;

            // Lifetime invariant:
            // DiagnosticList/Diagnostic spans can reference arena-owned storage.
            // Never let a diagnostic span outlive the owning LintResult/AstArena.
            // Always serialize (or copy) diagnostics before disposing those owners.

            // Action metadata files (action.yml) require classified parsing.
            if (DocumentKindClassifier.GetPathHintKind(filePath) == DocumentKind.ActionMetadata)
            {
                result = LintActionMetadataToJsonUtf8(utf8Yaml, filePath, config);
            }
            else
            {
                using var parseResult = WorkflowParser.Parse(utf8Yaml, filePath);
                using (var lintResult = Engine.Check(parseResult, utf8Yaml, filePath, config))
                {
                    result = SerializeDiagnosticsToResult(lintResult.Diagnostics.AsSpan());
                }
            }

            // Cache for content-hash short circuit
            _lastYamlHash = yamlHash;
            _lastFilePath = filePath;
            _lastJsonOutput = result;

            return result;
        }
    }

    /// <summary>
    /// Parses a workflow once and returns its lint diagnostics plus flow-json in one UTF-8 JSON response.
    /// This is used only while the Flow tab is active; ordinary linting must not materialize flow data.
    /// </summary>
    public static byte[] RunToJsonWithFlowJsonUtf8(string yamlSource, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        lock (EngineGate)
        {
            var (utf8Yaml, _) = PlaygroundUtf8Scratch.EncodeAndHash(yamlSource, ref _utf8Yaml);
            var config = ActiveConfig;

            if (DocumentKindClassifier.GetPathHintKind(filePath) == DocumentKind.ActionMetadata)
            {
                return SerializeLintAndEmptyFlow(utf8Yaml, filePath, config);
            }

            using var parseResult = WorkflowParser.Parse(utf8Yaml, filePath);
            using var lintResult = Engine.Check(parseResult, utf8Yaml, filePath, config);
            var flow = WorkflowFlowCollector.Collect(parseResult, filePath);
            return SerializeLintAndFlow(lintResult.Diagnostics.AsSpan(), flow);
        }
    }

    /// <summary>
    /// Parses a workflow once and returns its lint diagnostics plus Mermaid text in one UTF-8 JSON response.
    /// This is used only while the Mermaid tab is active.
    /// </summary>
    public static byte[] RunToJsonWithMermaidUtf8(string yamlSource, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        lock (EngineGate)
        {
            var (utf8Yaml, _) = PlaygroundUtf8Scratch.EncodeAndHash(yamlSource, ref _utf8Yaml);
            var config = ActiveConfig;

            if (DocumentKindClassifier.GetPathHintKind(filePath) == DocumentKind.ActionMetadata)
            {
                return SerializeLintAndMermaid(utf8Yaml, filePath, config, flow: null);
            }

            using var parseResult = WorkflowParser.Parse(utf8Yaml, filePath);
            using var lintResult = Engine.Check(parseResult, utf8Yaml, filePath, config);
            var flow = WorkflowFlowCollector.Collect(parseResult, filePath);
            return SerializeLintAndMermaid(lintResult.Diagnostics.AsSpan(), flow);
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
        ResetIfOversized(ref JsonBuffer);
        JsonBuffer.Clear();
        using (var writer = new Utf8JsonWriter(JsonBuffer, CamelCaseWriterOptions))
        {
            WriteDiagnosticsArray(writer, diagnostics);
        }

        var written = JsonBuffer.WrittenSpan;
        if (written.IsEmpty)
        {
            return EmptyJsonArray;
        }

        return CopyToReusableOutput(written, ref _diagnosticsJsonBuf);
    }

    private static byte[] SerializeLintAndEmptyFlow(byte[] utf8Yaml, string filePath, LintConfig config)
    {
        var classifiedResult = WorkflowParser.ParseClassified(utf8Yaml, filePath, out var arena);
        try
        {
            var lintResult = Engine.CheckWithParseResult(utf8Yaml, filePath, config, classifiedResult.ParseResult, arena);
            return SerializeLintAndFlow(lintResult.Diagnostics.AsSpan(), flow: null);
        }
        finally
        {
            arena?.Dispose();
        }
    }

    private static byte[] SerializeLintAndFlow(ReadOnlySpan<Diagnostic> diagnostics, WorkflowFlow? flow)
    {
        ResetIfOversized(ref LintFlowJsonBuffer);
        LintFlowJsonBuffer.Clear();
        using (var writer = new Utf8JsonWriter(LintFlowJsonBuffer, CamelCaseWriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("diagnostics"u8);
            WriteDiagnosticsArray(writer, diagnostics);
            writer.WritePropertyName("flow"u8);
            WorkflowFlowJson.WriteDocument(writer, flow);
            writer.WriteEndObject();
        }

        return CopyToReusableOutput(LintFlowJsonBuffer.WrittenSpan, ref _lintFlowJsonOutput);
    }

    private static byte[] SerializeLintAndMermaid(byte[] utf8Yaml, string filePath, LintConfig config, WorkflowFlow? flow)
    {
        var classifiedResult = WorkflowParser.ParseClassified(utf8Yaml, filePath, out var arena);
        try
        {
            var lintResult = Engine.CheckWithParseResult(utf8Yaml, filePath, config, classifiedResult.ParseResult, arena);
            return SerializeLintAndMermaid(lintResult.Diagnostics.AsSpan(), flow);
        }
        finally
        {
            arena?.Dispose();
        }
    }

    private static byte[] SerializeLintAndMermaid(ReadOnlySpan<Diagnostic> diagnostics, WorkflowFlow? flow)
    {
        ResetIfOversized(ref MermaidBuffer);
        MermaidBuffer.Clear();
        if (flow is null)
        {
            WorkflowFlowMermaid.WriteEmpty(MermaidBuffer);
        }
        else
        {
            WorkflowFlowMermaid.Write(MermaidBuffer, flow);
        }

        ResetIfOversized(ref LintFlowJsonBuffer);
        LintFlowJsonBuffer.Clear();
        using (var writer = new Utf8JsonWriter(LintFlowJsonBuffer, CamelCaseWriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("diagnostics"u8);
            WriteDiagnosticsArray(writer, diagnostics);
            writer.WriteString("mermaid"u8, MermaidBuffer.WrittenSpan);
            writer.WriteEndObject();
        }

        return CopyToReusableOutput(LintFlowJsonBuffer.WrittenSpan, ref _lintFlowJsonOutput);
    }

    private static byte[] CopyToReusableOutput(ReadOnlySpan<byte> written, ref byte[] cache)
    {
        if (written.Length > MaxRetainedBufferBytes)
        {
            return written.ToArray();
        }

        if (cache.Length != written.Length)
        {
            cache = new byte[written.Length];
        }

        if (!written.SequenceEqual(cache))
        {
            written.CopyTo(cache);
        }

        return cache;
    }

    private static void ResetIfOversized(ref ArrayBufferWriter<byte> buffer)
    {
        if (buffer.Capacity > MaxRetainedBufferBytes)
        {
            buffer = new ArrayBufferWriter<byte>(4096);
        }
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

        // Create PinRemediationEngine with resolvers (read overrides under lock — shared with tests).
        IActionShaResolver? actionResolver;
        IImageDigestResolver? imageResolver;
        lock (EngineGate)
        {
            actionResolver = ActionShaResolverOverride ?? DefaultActionShaResolver;
            imageResolver = ImageDigestResolverOverride ?? DefaultImageDigestResolver;
        }

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
}

/// <summary>Result of async fix application including network-based pin remediation.</summary>
public readonly record struct AsyncFixResult(string Yaml, int ResolvedCount, int SkippedCount, int FailedCount);
