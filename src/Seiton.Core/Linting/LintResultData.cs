using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

/// <summary>Combined parse and lint result data for a single YAML document.</summary>
/// <remarks>
/// When returned via <see cref="LintResult"/> from <see cref="LintEngine.Check(byte[], string, LintConfig?)"/>, the
/// <see cref="Diagnostics"/> backing array is pooled and registered with the <see cref="AstArena"/>.
/// The array is returned to the pool when the owning result is disposed.
/// Call <see cref="CopyDiagnostics"/> to obtain a caller-owned copy that is safe to retain
/// beyond the result's lifetime.
/// </remarks>
internal readonly record struct LintResultData(
    ParseResultData ParseResult,
    DiagnosticList Diagnostics)
{
    /// <summary>
    /// Gets the number of diagnostics in <see cref="Diagnostics"/>.
    /// </summary>
    public int DiagnosticCount => Diagnostics.Length;

    /// <summary>Gets the summary of suppressed diagnostics from inline and exclusion rules.</summary>
    public SuppressionSummary SuppressionSummary { get; init; } = SuppressionSummary.Empty;

    /// <summary>Gets the document kind (workflow or action metadata) used during linting.</summary>
    public DocumentKind DocumentKind { get; init; }

    /// <summary>Gets the number of rules that were active (enabled and applicable) for this document.</summary>
    public int ActiveRuleCount { get; init; }

    /// <summary>Gets the number of rules that were disabled by config or opt-in status (not by document kind mismatch).</summary>
    public int DisabledRuleCount { get; init; }

    /// <summary>
    /// Gets the IDs of rules that were disabled by config or opt-in status.
    /// The array length matches <see cref="DisabledRuleCount"/>.
    /// </summary>
    public string[] DisabledRuleIds { get; init; } = [];

    /// <summary>Gets the parsed workflow AST, if the document is a workflow file.</summary>
    public Workflow? Workflow => ParseResult.Workflow;

    /// <summary>Gets the parsed action metadata AST, if the document is an action file.</summary>
    public ActionMetadata? ActionMetadata => ParseResult.ActionMetadata;

    /// <summary>Gets whether the parse result contains a fatal error that prevents linting.</summary>
    public bool HasFatalError => ParseResult.HasFatalError;

    /// <summary>Gets the diagnostics produced during the parsing phase.</summary>
    public DiagnosticList ParseDiagnostics => ParseResult.Diagnostics;

    /// <summary>Gets whether any diagnostics have an associated auto-fix.</summary>
    public bool HasFixableDiagnostics => FixableDiagnosticCount > 0;

    /// <summary>Gets the number of diagnostics that have an associated auto-fix.</summary>
    public int FixableDiagnosticCount
    {
        get
        {
            var count = 0;
            var len = DiagnosticCount;
            for (var i = 0; i < len; i++)
            {
                if (Diagnostics[i].Fix is not null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Gets all diagnostics that have an associated auto-fix.</summary>
    public Diagnostic[] FixableDiagnostics
    {
        get
        {
            if (DiagnosticCount == 0)
            {
                return [];
            }

            var result = new Diagnostic[FixableDiagnosticCount];
            var index = 0;
            var len = DiagnosticCount;
            for (var i = 0; i < len; i++)
            {
                if (Diagnostics[i].Fix is null)
                {
                    continue;
                }

                result[index++] = Diagnostics[i];
            }

            return result;
        }
    }

    /// <summary>Gets all auto-fix objects from fixable diagnostics.</summary>
    public DiagnosticFix[] Fixes
    {
        get
        {
            if (DiagnosticCount == 0)
            {
                return [];
            }

            var result = new DiagnosticFix[FixableDiagnosticCount];
            var index = 0;
            var len = DiagnosticCount;
            for (var i = 0; i < len; i++)
            {
                var fix = Diagnostics[i].Fix;
                if (fix is null)
                {
                    continue;
                }

                result[index++] = fix.Value;
            }

            return result;
        }
    }

    /// <summary>
    /// Returns a caller-owned copy of the <see cref="Diagnostics"/> collection.
    /// The returned <see cref="OwnedDiagnostics"/> is safe to retain indefinitely,
    /// unlike <see cref="Diagnostics"/> which may reference arena-pooled memory.
    /// </summary>
    public OwnedDiagnostics CopyDiagnostics()
    {
        if (DiagnosticCount == 0)
        {
            return default;
        }

        return new OwnedDiagnostics(Diagnostics.AsSpan().ToArray());
    }
}

/// <summary>
/// Aggregated counts and per-rule breakdown of suppressed diagnostics.
/// <para>
/// Aggregated multi-file summaries may intentionally leave <see cref="Records"/> empty
/// while still preserving <see cref="TotalSuppressed"/> and <see cref="SuppressedByRule"/>.
/// </para>
/// </summary>
public readonly record struct SuppressionSummary(
    int TotalSuppressed,
    IReadOnlyDictionary<string, int> SuppressedByRule,
    SuppressionRecord[] Records)
{
    /// <summary>
    /// Gets the number of valid records in <see cref="Records"/>.
    /// The backing array may be oversized when a reusable buffer is used.
    /// </summary>
    public int RecordCount { get; init; } = Records.Length;

    /// <summary>Gets an empty suppression summary with no suppressed diagnostics.</summary>
    public static SuppressionSummary Empty { get; } = new(0, new Dictionary<string, int>(StringComparer.Ordinal), []);
}

/// <summary>A single suppression event recording which rule was suppressed and where.</summary>
public readonly record struct SuppressionRecord(
    string RuleId,
    SuppressionSource Source,
    int SourceLine,
    int SourceColumn,
    int DiagnosticLine,
    int DiagnosticColumn);

public enum SuppressionSource
{
    InlineNextLine,
    InlineJob,
    InlineFile,
    ConfigFile,
    ConfigJob,
}
