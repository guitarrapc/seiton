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
    TextRange Location);

public readonly record struct ParseResult(
    Workflow? Workflow,
    Diagnostic[] Diagnostics,
    bool HasFatalError);
