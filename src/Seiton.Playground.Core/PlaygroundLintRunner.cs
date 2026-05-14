using System.Buffers;
using System.Text;
using System.Text.Json;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
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
/// enormously increasing GC pressure in the constrained WASM heap (see plan_playground_crush.md).
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

    /// <summary>Cached last input source string for identity-based short circuit. Guarded by <see cref="EngineGate"/>.</summary>
    private static string? _lastYamlSource;

    /// <summary>Cached last file path for identity-based short circuit. Guarded by <see cref="EngineGate"/>.</summary>
    private static string? _lastFilePath;

    /// <summary>Cached last JSON output for identity-based short circuit. Guarded by <see cref="EngineGate"/>.</summary>
    private static byte[]? _lastJsonOutput;

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

            ReadOnlySpan<Diagnostic> diagnosticsToSerialize;
            AstArena? ownedArena = null;

            // Action metadata files (action.yml) require classified parsing — not incremental.
            if (DocumentKindClassifier.GetPathHintKind(filePath) == DocumentKind.ActionMetadata)
            {
                var classifiedResult = WorkflowParser.ParseClassified(utf8Yaml, filePath, out ownedArena);
                var lintResult = Engine.CheckWithParseResult(utf8Yaml, filePath, LintWithFixMetadata, classifiedResult.ParseResult, ownedArena);
                diagnosticsToSerialize = lintResult.Diagnostics.AsSpan();
            }
            else
            {
                // D-5b/5c: Use incremental parse to skip unchanged root sections and jobs
                var parseResult = IncrementalCtx.ParseIncrementally(utf8Yaml, filePath);

                // D-5d: Build skip mask — reused jobs with cached diagnostics skip lint
                var jobCount = parseResult.Workflow?.Jobs.Count ?? 0;
                var skipJobs = IncrementalCtx.BuildSkipJobs(jobCount, parseResult.Workflow);

                // Lint with optional job skipping
                var lintResult = Engine.CheckWithParseResult(utf8Yaml, filePath, LintWithFixMetadata, parseResult.Data, IncrementalCtx.Arena, skipJobs);

                // Merge fresh diagnostics with cached diagnostics for skipped jobs
                diagnosticsToSerialize = IncrementalCtx.MergeDiagnosticsWithCache(lintResult.Diagnostics, skipJobs);
            }

            JsonBuffer.Clear();
            using (var writer = new Utf8JsonWriter(JsonBuffer, CamelCaseWriterOptions))
            {
                WriteDiagnosticsArray(writer, diagnosticsToSerialize);
            }

            // Dispose arena for ActionMetadata path (not owned by IncrementalParseContext)
            ownedArena?.Dispose();

            // NOTE: Incremental path arena is NOT disposed — IncrementalParseContext owns it for reuse
            var written = JsonBuffer.WrittenSpan;

            // Reuse previous buffer when content is identical to avoid per-call byte[] allocation.
            // When content differs we always allocate a new array so that callers who retain
            // a previous result never observe their buffer mutating.
            byte[] result;
            if (_lastJsonOutput is not null && written.SequenceEqual(_lastJsonOutput))
            {
                // Content identical — return cached output (zero alloc)
                result = _lastJsonOutput;
            }
            else
            {
                // Content or length changed — allocate new array
                result = written.ToArray();
            }

            // Cache for identity-based short circuit
            _lastYamlSource = yamlSource;
            _lastFilePath = filePath;
            _lastJsonOutput = result;

            return result;
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
                using var result = Engine.Check(current, filePath, LintWithFixMetadata);
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
