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
        // Should contain per-rule breakdown in table format sorted by count descending
        await Assert.That(output).Contains("| unpinned-uses");
        await Assert.That(output).Contains("| template-injection");
        await Assert.That(output).Contains("| job-permissions-required");
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

        await Assert.That(output).Contains("| unpinned-uses");
        // Parser diagnostics (null RuleId) should not appear as a rule count
        await Assert.That(output).DoesNotContain("null");
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

    // === Per-File Breakdown Tests ===

    [Test]
    public async Task WriteSummary_ShowsPerFileBreakdown_ByDefault()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "template-injection", FilePath: "/repo/workflow1.yml"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "unpinned-uses", FilePath: "/repo/workflow1.yml"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 3, 1, 3, 2), RuleId: "unpinned-uses", FilePath: "/repo/workflow1.yml"),
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 4, 1, 4, 2), RuleId: "template-injection", FilePath: "/repo/workflow2.yml"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 5, verbose: false);
        var output = sw.ToString();

        // Should show total summary
        await Assert.That(output).Contains("2 errors, 2 warnings in 5 files");
        // Should show per-file breakdown as table
        await Assert.That(output).Contains("| workflow1.yml");
        await Assert.That(output).Contains("| workflow2.yml");
    }

    [Test]
    public async Task WriteSummary_PerFileBreakdown_SortedByCountDescending()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "rule-a", FilePath: "/repo/few.yml"),
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "rule-b", FilePath: "/repo/many.yml"),
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 3, 1, 3, 2), RuleId: "rule-b", FilePath: "/repo/many.yml"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 4, 1, 4, 2), RuleId: "rule-a", FilePath: "/repo/many.yml"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 3, verbose: false);
        var output = sw.ToString();

        // many.yml (3 issues) should appear before few.yml (1 issue)
        var manyIndex = output.IndexOf("many.yml");
        var fewIndex = output.IndexOf("few.yml");
        await Assert.That(manyIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(fewIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(manyIndex).IsLessThan(fewIndex);
    }

    [Test]
    public async Task WriteSummary_PerFileBreakdown_NotShown_WhenNoDiagnostics()
    {
        var diagnostics = new List<Diagnostic>();

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 5, verbose: false);
        var output = sw.ToString();

        // Only the total line, no per-file breakdown
        await Assert.That(output.Trim()).IsEqualTo("0 issues in 5 files");
    }

    [Test]
    public async Task WriteSummary_PerFileBreakdown_SkipsDiagnosticsWithoutFilePath()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "rule-a", FilePath: null),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "rule-b", FilePath: "/repo/has-path.yml"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 2, verbose: false);
        var output = sw.ToString();

        // Only file with path should appear in per-file breakdown
        await Assert.That(output).Contains("has-path.yml");
        // The null-path diagnostic is still counted in total but not per-file
        await Assert.That(output).Contains("1 error, 1 warning in 2 files");
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

    // === Per-Rule Breakdown Table Format Tests (6d) ===

    [Test]
    public async Task WriteSummary_Verbose_PerRuleBreakdown_UsesTableFormat()
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

        // Should use table format with pipe separators
        await Assert.That(output).Contains("| Rule");
        await Assert.That(output).Contains("| Count");
        // Should have separator row with dashes
        await Assert.That(output).Contains("|---");
        // Should contain rule data in table rows
        await Assert.That(output).Contains("| unpinned-uses");
        await Assert.That(output).Contains("| template-injection");
        await Assert.That(output).Contains("| job-permissions-required");
    }

    [Test]
    public async Task WriteSummary_Verbose_PerRuleBreakdown_CountsRightAligned()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "template-injection"),
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "template-injection"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 3, 1, 3, 2), RuleId: "unpinned-uses"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 2, verbose: true);
        var output = sw.ToString();

        // Separator should indicate right-alignment with trailing colon
        await Assert.That(output).Contains("---:|");
    }

    [Test]
    public async Task WriteSummary_Verbose_PerRuleBreakdown_SortedByCountDescending()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "rule-a"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "rule-b"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 3, 1, 3, 2), RuleId: "rule-b"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 4, 1, 4, 2), RuleId: "rule-b"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 5, 1, 5, 2), RuleId: "rule-a"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 2, verbose: true);
        var output = sw.ToString();

        // rule-b (3) should appear before rule-a (2)
        var ruleBIndex = output.IndexOf("| rule-b");
        var ruleAIndex = output.IndexOf("| rule-a");
        await Assert.That(ruleBIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(ruleAIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(ruleBIndex).IsLessThan(ruleAIndex);
    }

    [Test]
    public async Task WriteSummary_Verbose_PerRuleBreakdown_SeparatedByBlankLine()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "template-injection", FilePath: "/repo/ci.yml"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "unpinned-uses", FilePath: "/repo/ci.yml"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 1, verbose: true);
        var output = sw.ToString();

        // There should be a blank line before the rule table (separating from per-file)
        var ruleTableIndex = output.IndexOf("| Rule");
        await Assert.That(ruleTableIndex).IsGreaterThanOrEqualTo(0);
        // Check that the line before the rule table header is an empty line
        // (WriteSummary inserts writer.WriteLine() which produces an empty line)
        var beforeTable = output[..ruleTableIndex];
        var lines = beforeTable.Split('\n');
        // The last line before "| Rule" should be empty (or whitespace only)
        await Assert.That(lines[^1].Trim()).IsEqualTo("");
    }

    [Test]
    public async Task WriteSummary_Verbose_PerRuleBreakdown_RemainMode_UsesRemainingHeader()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "unpinned-uses"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "unpinned-uses"),
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 3, 1, 3, 2), RuleId: "template-injection"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 2, verbose: true, isRemainMode: true);
        var output = sw.ToString();

        // In remain mode (fix context), header should say "Remaining" not "Count"
        await Assert.That(output).Contains("| Remaining");
        await Assert.That(output).DoesNotContain("| Count");
    }

    [Test]
    public async Task WriteSummary_Verbose_PerRuleBreakdown_NormalMode_UsesCountHeader()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "unpinned-uses"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 1, verbose: true, isRemainMode: false);
        var output = sw.ToString();

        // In normal mode, header should say "Count"
        await Assert.That(output).Contains("| Count");
        await Assert.That(output).DoesNotContain("| Remaining");
    }

    // === Per-File Breakdown Table Format Tests (6b) ===

    [Test]
    public async Task WriteSummary_PerFileBreakdown_UsesTableFormat()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "template-injection", FilePath: "/repo/workflow1.yml"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "unpinned-uses", FilePath: "/repo/workflow1.yml"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 3, 1, 3, 2), RuleId: "unpinned-uses", FilePath: "/repo/workflow2.yml"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 3, verbose: false);
        var output = sw.ToString();

        // Should use table format with pipe separators
        await Assert.That(output).Contains("| File");
        await Assert.That(output).Contains("| Errors");
        await Assert.That(output).Contains("| Warnings");
        // Should have separator row
        await Assert.That(output).Contains("|---");
        // Should contain file data in table rows
        await Assert.That(output).Contains("| workflow1.yml");
        await Assert.That(output).Contains("| workflow2.yml");
    }

    [Test]
    public async Task WriteSummary_PerFileBreakdown_NumbersRightAligned()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "rule-a", FilePath: "/repo/ci.yml"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "rule-b", FilePath: "/repo/ci.yml"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 2, verbose: false);
        var output = sw.ToString();

        // Separator should indicate right-alignment with trailing colon for numeric columns
        await Assert.That(output).Contains("---:|");
    }

    [Test]
    public async Task WriteSummary_PerFileBreakdown_SortedByCountDescending_TableFormat()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "rule-a", FilePath: "/repo/few.yml"),
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "rule-b", FilePath: "/repo/many.yml"),
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 3, 1, 3, 2), RuleId: "rule-b", FilePath: "/repo/many.yml"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 4, 1, 4, 2), RuleId: "rule-a", FilePath: "/repo/many.yml"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 3, verbose: false);
        var output = sw.ToString();

        // many.yml (3 issues) should appear before few.yml (1 issue)
        var manyIndex = output.IndexOf("| many.yml");
        var fewIndex = output.IndexOf("| few.yml");
        await Assert.That(manyIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(fewIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(manyIndex).IsLessThan(fewIndex);
    }

    [Test]
    public async Task WriteSummary_PerFileBreakdown_ShowsZeroValues()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "rule-a", FilePath: "/repo/errors-only.yml"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "rule-b", FilePath: "/repo/warnings-only.yml"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 2, verbose: false);
        var output = sw.ToString();

        // File with only errors should show 0 for warnings
        // File with only warnings should show 0 for errors
        // (both should be present in table with 0 values)
        await Assert.That(output).Contains("| errors-only.yml");
        await Assert.That(output).Contains("| warnings-only.yml");
        // Check that 0 appears in the output (not empty cell)
        var lines = output.Split('\n');
        var errorsOnlyLine = lines.FirstOrDefault(l => l.Contains("errors-only.yml"));
        await Assert.That(errorsOnlyLine).IsNotNull();
        await Assert.That(errorsOnlyLine!).Contains("0");
    }

    [Test]
    public async Task WriteSummary_PerFileBreakdown_SeparatedByBlankLine()
    {
        var diagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "rule-a", FilePath: "/repo/ci.yml"),
        };

        using var sw = new StringWriter();
        CheckCommand.WriteSummary(sw, diagnostics, 2, verbose: false);
        var output = sw.ToString();

        // There should be a blank line between the summary line and the table
        var tableIndex = output.IndexOf("| File");
        await Assert.That(tableIndex).IsGreaterThanOrEqualTo(0);
        var beforeTable = output[..tableIndex];
        var lines = beforeTable.Split('\n');
        await Assert.That(lines[^1].Trim()).IsEqualTo("");
    }

    [Test]
    public async Task WriteFixSummary_PerFileDetail_UsesTableFormat()
    {
        var fixedFiles = new List<(string FilePath, int FixedCount)>
        {
            ("/repo/ci.yml", 3),
            ("/repo/release.yml", 2),
        };
        var remainingDiagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "rule-a", FilePath: "/repo/release.yml"),
        };

        using var sw = new StringWriter();
        FixCommand.WriteFixSummary(sw, fixedFiles, remainingDiagnostics, FixCommand.FixSummaryMode.Applied);
        var output = sw.ToString();

        // Should use table format with pipe separators
        await Assert.That(output).Contains("| File");
        await Assert.That(output).Contains("| Fixed");
        await Assert.That(output).Contains("| Remaining");
        await Assert.That(output).Contains("|---");
        await Assert.That(output).Contains("| ci.yml");
        await Assert.That(output).Contains("| release.yml");
    }

    [Test]
    public async Task WriteFixSummary_DryRun_UsesWouldFixHeader()
    {
        var fixedFiles = new List<(string FilePath, int FixedCount)>
        {
            ("/repo/ci.yml", 5),
        };
        var remainingDiagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "rule-a", FilePath: "/repo/ci.yml"),
        };

        using var sw = new StringWriter();
        FixCommand.WriteFixSummary(sw, fixedFiles, remainingDiagnostics, FixCommand.FixSummaryMode.DryRun);
        var output = sw.ToString();

        // In dry-run mode, header should say "Would Fix"
        await Assert.That(output).Contains("| Would Fix");
        await Assert.That(output).DoesNotContain("| Fixed |");
    }

    [Test]
    public async Task WriteFixSummary_Check_UsesFixableHeader()
    {
        var fixedFiles = new List<(string FilePath, int FixedCount)>
        {
            ("/repo/ci.yml", 4),
        };
        var remainingDiagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "rule-a", FilePath: "/repo/ci.yml"),
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "rule-a", FilePath: "/repo/ci.yml"),
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 3, 1, 3, 2), RuleId: "rule-a", FilePath: "/repo/ci.yml"),
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 4, 1, 4, 2), RuleId: "rule-a", FilePath: "/repo/ci.yml"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 5, 1, 5, 2), RuleId: "rule-b", FilePath: "/repo/ci.yml"),
        };

        using var sw = new StringWriter();
        FixCommand.WriteFixSummary(sw, fixedFiles, remainingDiagnostics, FixCommand.FixSummaryMode.Check);
        var output = sw.ToString();

        // In check mode, header should say "Fixable"
        await Assert.That(output).Contains("| Fixable");
        await Assert.That(output).DoesNotContain("| Fixed |");
        await Assert.That(output).DoesNotContain("| Would Fix");
    }

    [Test]
    public async Task WriteFixSummary_PerFileDetail_ShowsZeroRemaining()
    {
        var fixedFiles = new List<(string FilePath, int FixedCount)>
        {
            ("/repo/ci.yml", 3),
        };
        var remainingDiagnostics = new List<Diagnostic>();

        using var sw = new StringWriter();
        FixCommand.WriteFixSummary(sw, fixedFiles, remainingDiagnostics, FixCommand.FixSummaryMode.Applied);
        var output = sw.ToString();

        // Should show 0 for remaining (not hide the file)
        await Assert.That(output).Contains("| ci.yml");
        var lines = output.Split('\n');
        var ciLine = lines.FirstOrDefault(l => l.Contains("| ci.yml"));
        await Assert.That(ciLine).IsNotNull();
        await Assert.That(ciLine!).Contains("0");
    }

    [Test]
    public async Task WriteFixSummary_PerFileDetail_NumbersRightAligned()
    {
        var fixedFiles = new List<(string FilePath, int FixedCount)>
        {
            ("/repo/ci.yml", 10),
        };
        var remainingDiagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "rule-a", FilePath: "/repo/ci.yml"),
        };

        using var sw = new StringWriter();
        FixCommand.WriteFixSummary(sw, fixedFiles, remainingDiagnostics, FixCommand.FixSummaryMode.Applied);
        var output = sw.ToString();

        // Separator should indicate right-alignment for numeric columns
        await Assert.That(output).Contains("---:|");
    }

    [Test]
    public async Task WriteFixSummary_PerFileDetail_SortedByTotalDescending()
    {
        var fixedFiles = new List<(string FilePath, int FixedCount)>
        {
            ("/repo/few.yml", 1),
            ("/repo/many.yml", 5),
        };
        var remainingDiagnostics = new List<Diagnostic>
        {
            new(DiagnosticSeverity.Error, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "rule-a", FilePath: "/repo/few.yml"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 2, 1, 2, 2), RuleId: "rule-b", FilePath: "/repo/many.yml"),
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 3, 1, 3, 2), RuleId: "rule-b", FilePath: "/repo/many.yml"),
        };

        using var sw = new StringWriter();
        FixCommand.WriteFixSummary(sw, fixedFiles, remainingDiagnostics, FixCommand.FixSummaryMode.Applied);
        var output = sw.ToString();

        // many.yml (5 fixed + 2 remaining = 7 total) should appear before few.yml (1 fixed + 1 remaining = 2 total)
        var manyIndex = output.IndexOf("| many.yml");
        var fewIndex = output.IndexOf("| few.yml");
        await Assert.That(manyIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(fewIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(manyIndex).IsLessThan(fewIndex);
    }
}
