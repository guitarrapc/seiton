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

    /// <summary>
    /// Parses and lints <paramref name="yamlSource"/> as UTF-8 and returns a JSON array of diagnostics.
    /// </summary>
    /// <param name="yamlSource">Full YAML document text.</param>
    /// <param name="filePath">Virtual path used for document classification (e.g. workflow vs action).</param>
    /// <returns>UTF-8 JSON array of camelCase diagnostic objects.</returns>
    public static string RunToJson(string yamlSource, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlSource);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var utf8Yaml = Encoding.UTF8.GetBytes(yamlSource);
        // Hold the lock while reading result.Diagnostics — the engine's two-buffer
        // swap means the backing array is owned by the engine and a concurrent Check()
        // would overwrite it. Write JSON directly under the lock, then convert to string.
        lock (EngineGate)
        {
            var result = Engine.Check(utf8Yaml, filePath, LintWithFixMetadata);

            JsonBuffer.Clear();
            using (var writer = new Utf8JsonWriter(JsonBuffer, CamelCaseWriterOptions))
            {
                writer.WriteStartArray();
                for (var i = 0; i < result.Diagnostics.Length; i++)
                {
                    var d = result.Diagnostics[i];
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

            // Return the AstArena to the ThreadStatic cache immediately so the next
            // Rent() reuses it instead of allocating a new one. Without this, the arena
            // stays alive until GC collects the LintResult, doubling memory pressure
            // in the constrained WASM heap and causing OOM crashes.
            result.ParseResult.Arena?.Dispose();

            return Encoding.UTF8.GetString(JsonBuffer.WrittenSpan);
        }
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
                current = FixEngine.Apply(current, new[] { diag });

                // Dispose arena each pass so the next Check() reuses it via ThreadStatic cache.
                result.ParseResult.Arena?.Dispose();
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
}
