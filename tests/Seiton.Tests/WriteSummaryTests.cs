using Seiton.Commands;
using Seiton.Core.Parsing;

namespace Seiton.Tests;

public sealed class WriteSummaryTests
{
    [Test]
    public async Task WriteSummary_Verbose_ShowsPerRuleBreakdown()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "template-injection"),
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "template-injection"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 3, 1, 3, 2), RuleId: "unpinned-uses"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 4, 1, 4, 2), RuleId: "unpinned-uses"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 5, 1, 5, 2), RuleId: "unpinned-uses"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 6, 1, 6, 2), RuleId: "job-permissions-required"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 3, verbose: true);
        var output = sw.ToString();

        // Should contain the normal summary line
        await Assert.That(output).Contains("2 errors, 4 warnings in 3 files");
        // Should contain per-rule breakdown sorted by count descending
        await Assert.That(output).Contains("unpinned-uses: 3");
        await Assert.That(output).Contains("template-injection: 2");
        await Assert.That(output).Contains("job-permissions-required: 1");
    }

    [Test]
    public async Task WriteSummary_NotVerbose_DoesNotShowPerRuleBreakdown()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "template-injection"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "unpinned-uses"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 1, verbose: false);
        var output = sw.ToString();

        await Assert.That(output).Contains("1 error, 1 warning in 1 file");
        await Assert.That(output).DoesNotContain("template-injection:");
        await Assert.That(output).DoesNotContain("unpinned-uses:");
    }

    [Test]
    public async Task WriteSummary_Verbose_ZeroDiagnostics_NoBreakdown()
    {
        var diagnostics = new List<Diagnostic>();

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 5, verbose: true);
        var output = sw.ToString();

        await Assert.That(output).Contains("0 issues in 5 files");
        // No rule breakdown when there are no diagnostics
        await Assert.That(output.Trim()).IsEqualTo("0 issues in 5 files");
    }

    [Test]
    public async Task WriteSummary_Verbose_ParserDiagnosticsWithNullRuleId_GroupedSeparately()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "parse error", new TextRange(0, 1, 1, 1, 1, 2), RuleId: null),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "unpinned-uses"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 1, verbose: true);
        var output = sw.ToString();

        await Assert.That(output).Contains("unpinned-uses: 1");
        // Parser diagnostics (null RuleId) should not appear as a rule count
        await Assert.That(output).DoesNotContain("null");
        await Assert.That(output).DoesNotContain(": 0");
    }

    [Test]
    public async Task WriteSummary_WarningsOnly_ShowsMinSeverityHint()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "unpinned-uses"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "job-permissions-required"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 2, verbose: false, showExitHint: true);
        var output = sw.ToString();

        await Assert.That(output).Contains("2 warnings in 2 files");
        await Assert.That(output).Contains("--min-severity error");
    }

    [Test]
    public async Task WriteSummary_ErrorsAndWarnings_DoesNotShowMinSeverityHint()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "template-injection"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "unpinned-uses"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 1, verbose: false, showExitHint: true);
        var output = sw.ToString();

        await Assert.That(output).Contains("1 error, 1 warning in 1 file");
        await Assert.That(output).DoesNotContain("--min-severity");
    }

    [Test]
    public async Task WriteSummary_WarningsOnly_ShowExitHintFalse_NoHint()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "unpinned-uses"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 1, verbose: false, showExitHint: false);
        var output = sw.ToString();

        await Assert.That(output).Contains("1 warning in 1 file");
        await Assert.That(output).DoesNotContain("--min-severity");
    }

    [Test]
    public async Task WriteNetworkFixHint_UnpinnedUsesWithoutNetwork_ShowsHint()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "unpinned-uses"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteNetworkFixHint(sw, diagnostics, enablePinNetwork: false, enableImageNetwork: false);
        var output = sw.ToString();

        await Assert.That(output).Contains("--enable-pin-network");
    }

    [Test]
    public async Task WriteNetworkFixHint_UnpinnedImageWithoutNetwork_ShowsHint()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "unpinned-image"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteNetworkFixHint(sw, diagnostics, enablePinNetwork: false, enableImageNetwork: false);
        var output = sw.ToString();

        await Assert.That(output).Contains("--enable-image-network");
    }

    [Test]
    public async Task WriteNetworkFixHint_UnpinnedUsesWithNetworkEnabled_NoHint()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "unpinned-uses"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteNetworkFixHint(sw, diagnostics, enablePinNetwork: true, enableImageNetwork: false);
        var output = sw.ToString();

        await Assert.That(output).IsEqualTo("");
    }

    [Test]
    public async Task WriteNetworkFixHint_NoDiagnosticsRequiringNetwork_NoHint()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "job-permissions-required"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteNetworkFixHint(sw, diagnostics, enablePinNetwork: false, enableImageNetwork: false);
        var output = sw.ToString();

        await Assert.That(output).IsEqualTo("");
    }
}
