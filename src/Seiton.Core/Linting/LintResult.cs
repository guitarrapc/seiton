using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public readonly record struct LintResult(
    ParseResult ParseResult,
    Diagnostic[] Diagnostics)
{
    public SuppressionSummary SuppressionSummary { get; init; } = SuppressionSummary.Empty;

    public Workflow? Workflow => ParseResult.Workflow;

    public ActionMetadata? ActionMetadata => ParseResult.ActionMetadata;

    public bool HasFatalError => ParseResult.HasFatalError;

    public Diagnostic[] ParseDiagnostics => ParseResult.Diagnostics;

    public bool HasFixableDiagnostics => FixableDiagnosticCount > 0;

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

public readonly record struct SuppressionSummary(
    int TotalSuppressed,
    IReadOnlyDictionary<string, int> SuppressedByRule,
    SuppressionRecord[] Records)
{
    public static SuppressionSummary Empty { get; } = new(0, new Dictionary<string, int>(StringComparer.Ordinal), []);
}

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
