namespace Seiton.Cli.Parsing;

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
    WorkflowDocument Workflow,
    Diagnostic[] Diagnostics,
    bool HasFatalError);
