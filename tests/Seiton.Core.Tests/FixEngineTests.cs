using Seiton.Core.Linting.Fixing;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Tests;

public sealed class FixEngineTests
{
    [Test]
    public async Task Apply_AppliesDiagnosticFixCollection()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var fixes = new[]
        {
            new DiagnosticFix("first", [new TextEdit(2, 2, "AB")]),
            new DiagnosticFix("second", [new TextEdit(7, 2, "YZ")]),
        };

        var result = FixEngine.Apply(source, fixes);

        await Assert.That(Encoding.UTF8.GetString(result)).IsEqualTo("01AB456YZ9");
    }

    [Test]
    public async Task Apply_AppliesDiagnosticsWithFixAndIgnoresNoFixDiagnostics()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var diagnostics = new[]
        {
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "no fix",
                new TextRange(0, 0, 1, 1, 1, 1),
                RuleId: "x"),
            new Diagnostic(
                DiagnosticSeverity.Warning,
                "has fix",
                new TextRange(0, 0, 1, 1, 1, 1),
                RuleId: "x",
                Fix: new DiagnosticFix("replace", [new TextEdit(2, 2, "AB")]))
        };

        var result = FixEngine.Apply(source, diagnostics);

        await Assert.That(Encoding.UTF8.GetString(result)).IsEqualTo("01AB456789");
    }

    [Test]
    public async Task Apply_AppliesEditsInDescendingOffsetOrder()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var edits = new[]
        {
            new TextEdit(2, 2, "AB"),
            new TextEdit(7, 2, "YZ"),
        };

        var result = FixEngine.Apply(source, edits);

        await Assert.That(Encoding.UTF8.GetString(result)).IsEqualTo("01AB456YZ9");
    }

    [Test]
    public async Task Apply_RejectsOverlappingEdits()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var edits = new[]
        {
            new TextEdit(2, 4, "ABCD"),
            new TextEdit(5, 2, "YZ"),
        };

        await Assert.That(() => FixEngine.Apply(source, edits)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task DetectDominantLineEnding_PrefersCrLfWhenMajority()
    {
        var source = Encoding.UTF8.GetBytes("a\r\nb\r\nc\n");

        var lineEnding = FixFormatting.DetectDominantLineEnding(source);

        await Assert.That(lineEnding).IsEqualTo("\r\n");
    }

    [Test]
    public async Task InferIndentation_PrefersSiblingIndentation()
    {
        var source = "jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n";

        var indentation = FixFormatting.InferIndentation(source, siblingLineNumber: 3, parentLineNumber: 2);

        await Assert.That(indentation).IsEqualTo("    ");
    }

    [Test]
    public async Task InferIndentation_FallsBackToParentPlusIndentationUnit()
    {
        var source = "jobs:\n  build:\n";

        var indentation = FixFormatting.InferIndentation(source, siblingLineNumber: null, parentLineNumber: 2);

        await Assert.That(indentation).IsEqualTo("    ");
    }

    [Test]
    public async Task DetectQuoteStyle_UsesSourceBytesAroundRange()
    {
        var source = Encoding.UTF8.GetBytes("name: 'value'\n");

        var quoteStyle = FixFormatting.DetectQuoteStyle(
            source,
            new TextRange(7, 5, 1, 8, 1, 13),
            quoted: true);

        await Assert.That(quoteStyle).IsEqualTo(ScalarQuoteStyle.SingleQuoted);
    }

    [Test]
    public async Task LintResult_Fixes_ReturnsOnlyFixPayloads()
    {
        var parseResult = new ParseResult(null, [], HasFatalError: false);
        var result = new LintResult(
            parseResult,
            [
                new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "no fix",
                    new TextRange(0, 0, 1, 1, 1, 1),
                    RuleId: "a"),
                new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "has fix",
                    new TextRange(0, 0, 1, 1, 1, 1),
                    RuleId: "b",
                    Fix: new DiagnosticFix("replace", [new TextEdit(1, 1, "X")]))
            ]);

        await Assert.That(result.Fixes.Length).IsEqualTo(1);
        await Assert.That(result.Fixes[0].Description).IsEqualTo("replace");
    }

    [Test]
    public async Task ApplyAndRelint_ClearsSelectedDiagnostics()
    {
        var yaml = """
        on: push
        permissions: write-all
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ok
        """;

        var source = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new DenyWriteAllRule()]);
        var before = engine.Check(source, "revalidate-deny.yml");

        var revalidated = FixEngine.ApplyAndRelint(engine, source, "revalidate-deny.yml", before.FixableDiagnostics);

        await Assert.That(revalidated.Before.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsFalse();
        await Assert.That(revalidated.After.HasFatalError).IsFalse();
    }

    [Test]
    public async Task ApplyAndRelint_ThrowsWhenFatalParseErrorIsIntroduced()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ok
        """;

        var source = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobStructureRule()]);
        var fixes = new[]
        {
            new DiagnosticFix("break yaml", [new TextEdit(0, source.Length, "[]")]),
        };

        await Assert.That(() => FixEngine.ApplyAndRelint(engine, source, "revalidate-fatal.yml", fixes))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ApplyAndRelint_ThrowsOnOverlappingEditsBeforeRelint()
    {
        var source = Encoding.UTF8.GetBytes("0123456789");
        var engine = new LintEngine([new JobStructureRule()]);
        var fixes = new[]
        {
            new DiagnosticFix("left", [new TextEdit(2, 4, "ABCD")]),
            new DiagnosticFix("right", [new TextEdit(5, 2, "YZ")]),
        };

        await Assert.That(() => FixEngine.ApplyAndRelint(engine, source, "revalidate-overlap.yml", fixes))
            .Throws<InvalidOperationException>();
    }
}
