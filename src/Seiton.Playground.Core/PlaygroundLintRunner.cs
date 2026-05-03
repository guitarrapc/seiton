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

    /// <summary>Two-buffer swap for UTF-8 YAML source. Guarded by <see cref="EngineGate"/>.
    /// IncrementalParseContext stores the previous buffer reference; we must not overwrite it.</summary>
    private static byte[] _utf8Buffer = new byte[4096];
    private static byte[] _utf8BufferPrev = new byte[4096];

    /// <summary>Cached severity display strings indexed by <see cref="DiagnosticSeverity"/>.</summary>
    private static readonly string[] SeverityStrings = ["Info", "Warning", "Error"];

    private static readonly JsonWriterOptions CamelCaseWriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Incremental parse context for D-5b root section reuse. Guarded by <see cref="EngineGate"/>.</summary>
    private static readonly IncrementalParseContext IncrementalCtx = new();

    /// <summary>Cached last input source string for identity-based short circuit. Guarded by <see cref="EngineGate"/>.</summary>
    private static string? _lastYamlSource;

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
            // Fast path: if source is identical to last call, return cached output (copy)
            if (ReferenceEquals(yamlSource, _lastYamlSource) && _lastJsonOutput is not null)
            {
                return (byte[])_lastJsonOutput.Clone();
            }

            var utf8Yaml = RentUtf8Buffer(yamlSource);

            // D-5b: Use incremental parse to skip unchanged root sections
            var parseResult = IncrementalCtx.ParseIncrementally(utf8Yaml, filePath);

            // Lint the (possibly incrementally-parsed) result
            var lintResult = Engine.CheckWithParseResult(utf8Yaml, filePath, LintWithFixMetadata, parseResult);

            JsonBuffer.Clear();
            using (var writer = new Utf8JsonWriter(JsonBuffer, CamelCaseWriterOptions))
            {
                WriteDiagnosticsArray(writer, lintResult.Diagnostics.AsSpan());
            }

            // NOTE: Arena is NOT disposed here — IncrementalParseContext owns it for reuse
            var result = JsonBuffer.WrittenSpan.ToArray();

            // Cache for identity-based short circuit
            _lastYamlSource = yamlSource;
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
                var result = Engine.Check(current, filePath, LintWithFixMetadata);
                if (!result.HasFixableDiagnostics)
                {
                    result.ParseResult.Arena?.Dispose();
                    return Encoding.UTF8.GetString(current);
                }

                var filtered = CollectAutoApplicableFixes(result.FixableDiagnostics);
                if (filtered.Length == 0)
                {
                    // Still has diagnostics with fixes attached, but none we auto-apply here (see CollectAutoApplicableFixes).
                    result.ParseResult.Arena?.Dispose();
                    return Encoding.UTF8.GetString(current);
                }

                var diag = PickNextDiagnosticToApply(filtered);
                try
                {
                    current = FixEngine.Apply(current, new[] { diag });
                }
                finally
                {
                    // Dispose arena each pass so the next Check() reuses it via ThreadStatic cache.
                    // Must be in finally so the arena is returned even if Apply throws.
                    result.ParseResult.Arena?.Dispose();
                }
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
    /// Returns an exact-sized UTF-8 byte[] for the given string, reusing the static buffer
    /// when the byte length matches exactly. Must be called under <see cref="EngineGate"/>.
    /// The parser requires exact-length arrays because VYaml reads utf8Yaml.AsMemory() fully.
    /// </summary>
    private static byte[] RentUtf8Buffer(string source)
    {
        var byteCount = Encoding.UTF8.GetByteCount(source);

        // Swap buffers: previous becomes prev, current gets (re)allocated if needed.
        // This ensures IncrementalParseContext._previousSource is never overwritten.
        (_utf8Buffer, _utf8BufferPrev) = (_utf8BufferPrev, _utf8Buffer);

        if (_utf8Buffer.Length != byteCount)
        {
            _utf8Buffer = new byte[byteCount];
        }

        Encoding.UTF8.GetBytes(source, _utf8Buffer);
        return _utf8Buffer;
    }
}
