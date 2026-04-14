namespace Seiton.Core.Parsing;

using Seiton.Core.Parsing.Ast;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public readonly record struct Diagnostic(
    DiagnosticSeverity Severity,
    string Message,
    TextRange Location,
    string? RuleId = null,
    TextRange[]? RelatedLocations = null,
    string? Help = null,
    string? FilePath = null);

public readonly record struct ParseResult(
    Workflow? Workflow,
    Diagnostic[] Diagnostics,
    bool HasFatalError);
