using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class FixModelTests
{
    [Test]
    public async Task Diagnostic_CanCarryOptionalFixPayload()
    {
        var fix = new DiagnosticFix(
            "replace write-all with read-all",
            [new TextEdit(12, 9, "read-all")]);

        var diagnostic = new Diagnostic(
            DiagnosticSeverity.Error,
            "permissions must not use write-all",
            new TextRange(12, 9, 2, 16, 2, 25),
            RuleId: "deny-write-all",
            FilePath: "workflow.yml",
            Fix: fix);

        await Assert.That(diagnostic.Fix).IsNotNull();
        await Assert.That(diagnostic.Fix!.Value.Description).IsEqualTo("replace write-all with read-all");
        await Assert.That(diagnostic.Fix.Value.Edits).HasSingleItem();
        await Assert.That(diagnostic.Fix.Value.Edits[0].Offset).IsEqualTo(12);
        await Assert.That(diagnostic.Fix.Value.Edits[0].Length).IsEqualTo(9);
        await Assert.That(diagnostic.Fix.Value.Edits[0].NewText).IsEqualTo("read-all");
    }

    [Test]
    public async Task LintResult_ExposesFixableDiagnosticsAndCount()
    {
        var diagnostics = new[]
        {
            new Diagnostic(
                DiagnosticSeverity.Error,
                "permissions must not use write-all",
                new TextRange(12, 9, 2, 16, 2, 25),
                RuleId: "deny-write-all",
                FilePath: "workflow.yml",
                Fix: new DiagnosticFix("replace write-all with read-all", [new TextEdit(12, 9, "read-all") ])),
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "job should declare permissions",
                new TextRange(0, 0, 4, 3, 4, 3),
                RuleId: "job-permissions-required",
                FilePath: "workflow.yml"),
        };

        var result = new LintResultData(
            new ParseResultData(new Parsing.Ast.Workflow(), null, [], false),
            diagnostics);

        await Assert.That(result.HasFixableDiagnostics).IsTrue();
        await Assert.That(result.FixableDiagnosticCount).IsEqualTo(1);
        await Assert.That(result.FixableDiagnostics).HasSingleItem();
        await Assert.That(result.FixableDiagnostics[0].RuleId).IsEqualTo("deny-write-all");
    }

    [Test]
    public async Task LintResult_WithoutFixes_ReportsEmptyFixableDiagnostics()
    {
        var diagnostics = new[]
        {
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "job should declare permissions",
                new TextRange(0, 0, 4, 3, 4, 3),
                RuleId: "job-permissions-required",
                FilePath: "workflow.yml"),
        };

        var result = new LintResultData(
            new ParseResultData(new Parsing.Ast.Workflow(), null, [], false),
            diagnostics);

        await Assert.That(result.HasFixableDiagnostics).IsFalse();
        await Assert.That(result.FixableDiagnosticCount).IsEqualTo(0);
        await Assert.That(result.FixableDiagnostics).IsEmpty();
    }
}
