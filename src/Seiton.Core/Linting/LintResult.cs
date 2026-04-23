using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

/// <summary>Combined parse and lint result for a single YAML document.</summary>
public readonly record struct LintResult(
    ParseResult ParseResult,
    Diagnostic[] Diagnostics)
{
    /// <summary>Gets the summary of suppressed diagnostics from inline and exclusion rules.</summary>
    public SuppressionSummary SuppressionSummary { get; init; } = SuppressionSummary.Empty;

    /// <summary>Gets the parsed workflow AST, if the document is a workflow file.</summary>
    public Workflow? Workflow => ParseResult.Workflow;

    /// <summary>Gets the parsed action metadata AST, if the document is an action file.</summary>
    public ActionMetadata? ActionMetadata => ParseResult.ActionMetadata;

    /// <summary>Gets whether the parse result contains a fatal error that prevents linting.</summary>
    public bool HasFatalError => ParseResult.HasFatalError;

    /// <summary>Gets the diagnostics produced during the parsing phase.</summary>
    public Diagnostic[] ParseDiagnostics => ParseResult.Diagnostics;

    /// <summary>Gets whether any diagnostics have an associated auto-fix.</summary>
    public bool HasFixableDiagnostics => FixableDiagnosticCount > 0;

    /// <summary>Gets the number of diagnostics that have an associated auto-fix.</summary>
    public int FixableDiagnosticCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < Diagnostics.Length; i++)
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
            if (Diagnostics.Length == 0)
            {
                return [];
            }

            var result = new Diagnostic[FixableDiagnosticCount];
            var index = 0;
            for (var i = 0; i < Diagnostics.Length; i++)
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
            if (Diagnostics.Length == 0)
            {
                return [];
            }

            var result = new DiagnosticFix[FixableDiagnosticCount];
            var index = 0;
            for (var i = 0; i < Diagnostics.Length; i++)
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
}

/// <summary>Aggregated counts and per-rule breakdown of suppressed diagnostics.</summary>
public readonly record struct SuppressionSummary(
    int TotalSuppressed,
    IReadOnlyDictionary<string, int> SuppressedByRule,
    SuppressionRecord[] Records)
{
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
