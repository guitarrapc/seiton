using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public readonly record struct LintResult(
    ParseResult ParseResult,
    Diagnostic[] Diagnostics)
{
    public SuppressionSummary SuppressionSummary { get; init; } = SuppressionSummary.Empty;

    public Workflow? Workflow => ParseResult.Workflow;

    public bool HasFatalError => ParseResult.HasFatalError;

    public Diagnostic[] ParseDiagnostics => ParseResult.Diagnostics;
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
