using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public readonly record struct LintResult(
    ParseResult ParseResult,
    Diagnostic[] Diagnostics)
{
    public Workflow? Workflow => ParseResult.Workflow;

    public bool HasFatalError => ParseResult.HasFatalError;

    public Diagnostic[] ParseDiagnostics => ParseResult.Diagnostics;
}
