namespace Seiton.Core.Parsing;

using Seiton.Core.Parsing.Ast;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public readonly record struct TextEdit(
    int Offset,
    int Length,
    string NewText);

public readonly record struct DiagnosticFix(
    string Description,
    TextEdit[] Edits);

public readonly record struct Diagnostic(
    DiagnosticSeverity Severity,
    string Message,
    TextRange Location,
    string? RuleId = null,
    TextRange[]? RelatedLocations = null,
    string? Help = null,
    string? FilePath = null,
    DiagnosticFix? Fix = null);

public readonly record struct ParseResult(
    Workflow? Workflow,
    ActionMetadata? ActionMetadata,
    Diagnostic[] Diagnostics,
    bool HasFatalError);
