using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Tests;

/// <summary>
/// Tests that verify LintEngine.Check() produces correct results when called
/// repeatedly with different inputs, ensuring internal LintConfig reuse
/// (expression cache, line starts, config sub-objects) does not cause stale state.
/// </summary>
public sealed class LintEngineConfigReuseTests
{
    [Test]
    public async Task Check_DifferentYamlSources_ProducesCorrectDiagnosticsEachTime()
    {
        // When LintConfig is reused, lineStarts and expressionCache must be invalidated
        // for new source bytes. Different YAML sources should produce diagnostics
        // with correct line/column positions.
        var engine = new LintEngine();

        // Source 1: write-all on line 2
        var yaml1 = Encoding.UTF8.GetBytes("on: push\npermissions: write-all\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n");
        var result1 = engine.Check(yaml1, ".github/workflows/a.yml");

        // Source 2: write-all on line 4 (preceded by extra blank lines)
        var yaml2 = Encoding.UTF8.GetBytes("on: push\n\n\npermissions: write-all\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n");
        var result2 = engine.Check(yaml2, ".github/workflows/b.yml");

        // Both should detect write-all, but at different lines
        var writeAll1 = result1.Diagnostics.FirstOrDefault(d => d.RuleId == "deny-write-all");
        var writeAll2 = result2.Diagnostics.FirstOrDefault(d => d.RuleId == "deny-write-all");

        await Assert.That(writeAll1.Message).IsNotNull();
        await Assert.That(writeAll2.Message).IsNotNull();
        await Assert.That(writeAll1.Location.StartLine).IsEqualTo(2);
        await Assert.That(writeAll2.Location.StartLine).IsEqualTo(4);
    }

    [Test]
    public async Task Check_WithConfigThenWithoutConfig_ProducesSameDiagnosticCount()
    {
        // When LintConfig is reused, switching from a call with explicit config
        // to one without must not corrupt results.
        var engine = new LintEngine();
        var yaml = Encoding.UTF8.GetBytes("on: push\npermissions: write-all\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n");

        // Call with explicit config
        var configWithFix = new LintConfig { Fix = new FixConfig { Enabled = true } };
        var result1 = engine.Check(yaml, ".github/workflows/test.yml", configWithFix);

        // Call without config
        var result2 = engine.Check(yaml, ".github/workflows/test.yml", config: null);

        // Both calls should produce the same diagnostic count and messages
        await Assert.That(result1.Diagnostics.Length).IsEqualTo(result2.Diagnostics.Length);
        for (var i = 0; i < result1.Diagnostics.Length; i++)
        {
            await Assert.That(result1.Diagnostics[i].Message).IsEqualTo(result2.Diagnostics[i].Message);
            await Assert.That(result1.Diagnostics[i].Location.StartLine).IsEqualTo(result2.Diagnostics[i].Location.StartLine);
        }
    }

    [Test]
    public async Task Check_RepeatedCallsSameSource_ProducesIdenticalResults()
    {
        // Reusing LintConfig across calls with the same source must produce
        // identical diagnostics every time.
        var engine = new LintEngine();
        var yaml = Encoding.UTF8.GetBytes("on: push\npermissions: write-all\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n");

        var result1 = engine.Check(yaml, ".github/workflows/ci.yml");
        var result2 = engine.Check(yaml, ".github/workflows/ci.yml");
        var result3 = engine.Check(yaml, ".github/workflows/ci.yml");

        await Assert.That(result1.Diagnostics.Length).IsEqualTo(result2.Diagnostics.Length);
        await Assert.That(result2.Diagnostics.Length).IsEqualTo(result3.Diagnostics.Length);

        for (var i = 0; i < result1.Diagnostics.Length; i++)
        {
            await Assert.That(result1.Diagnostics[i].Message).IsEqualTo(result2.Diagnostics[i].Message);
            await Assert.That(result2.Diagnostics[i].Message).IsEqualTo(result3.Diagnostics[i].Message);
            await Assert.That(result1.Diagnostics[i].Location.StartLine).IsEqualTo(result2.Diagnostics[i].Location.StartLine);
        }
    }

    [Test]
    public async Task Check_AlternatingSourcesWithExpressions_ExpressionCacheDoesNotLeak()
    {
        // Expression cache entries reference Utf8Yaml offsets. When source changes,
        // stale cache entries must not produce incorrect results.
        var engine = new LintEngine();

        // Source with an expression
        var yaml1 = Encoding.UTF8.GetBytes("on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ github.sha }}\n");
        // Different source with the same expression at a different offset
        var yaml2 = Encoding.UTF8.GetBytes("on:\n  pull_request:\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ github.sha }}\n");

        var result1 = engine.Check(yaml1, ".github/workflows/a.yml");
        var result2 = engine.Check(yaml2, ".github/workflows/b.yml");
        var result3 = engine.Check(yaml1, ".github/workflows/a.yml");

        // All calls should succeed without exceptions (no stale offset access)
        // and produce consistent results when re-checking the same source
        await Assert.That(result1.Diagnostics.Length).IsEqualTo(result3.Diagnostics.Length);
        for (var i = 0; i < result1.Diagnostics.Length; i++)
        {
            await Assert.That(result1.Diagnostics[i].Message).IsEqualTo(result3.Diagnostics[i].Message);
        }
    }

    [Test]
    public async Task Check_DifferentOutputSortOrder_SortsDiagnosticsCorrectly()
    {
        // When LintConfig is reused, OutputConfig.SortOrder must be properly
        // updated between calls.
        var engine = new LintEngine();
        var yaml = Encoding.UTF8.GetBytes("on: push\npermissions: write-all\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n      - run: echo ok\n");

        // First call with Location sort (default)
        var configLocation = new LintConfig
        {
            Output = new OutputConfig { SortOrder = DiagnosticSortOrder.Location },
        };
        var result1 = engine.Check(yaml, ".github/workflows/ci.yml", configLocation);

        // Second call with Rule sort
        var configRule = new LintConfig
        {
            Output = new OutputConfig { SortOrder = DiagnosticSortOrder.Rule },
        };
        var result2 = engine.Check(yaml, ".github/workflows/ci.yml", configRule);

        // Both should have the same diagnostics count
        await Assert.That(result1.Diagnostics.Length).IsEqualTo(result2.Diagnostics.Length);

        // Verify each result is sorted according to its configured comparator.
        await Assert.That(result1.Diagnostics.Length).IsGreaterThan(1);

        static string Signature(Diagnostic d) =>
            $"{d.RuleId}:{d.Location.StartLine}:{d.Location.StartColumn}";

        // Location sort: line → column → ruleId → message (matches CompareDiagnosticsByLocation)
        var actualLocationOrder = string.Join(",", result1.Diagnostics.Select(Signature));
        var expectedLocationOrder = string.Join(",",
            result1.Diagnostics
                .OrderBy(d => d.Location.StartLine)
                .ThenBy(d => d.Location.StartColumn)
                .ThenBy(d => d.RuleId, StringComparer.Ordinal)
                .ThenBy(d => d.Message, StringComparer.Ordinal)
                .Select(Signature));

        await Assert.That(actualLocationOrder).IsEqualTo(expectedLocationOrder);

        // Rule sort uses RuleCatalog.GetPriority (internal), so we can't replicate the
        // exact comparator in the test. Instead, verify the two sort orders actually differ,
        // proving that the SortOrder config is effective between calls.
        var ruleOrderSignature = string.Join(",", result2.Diagnostics.Select(Signature));
        await Assert.That(ruleOrderSignature).IsNotEqualTo(actualLocationOrder);
    }

    [Test]
    public async Task Check_DiagnosticCount_MatchesDiagnosticsArrayLength()
    {
        // DiagnosticCount must always equal Diagnostics.Length (exact-sized result arrays).
        var engine = new LintEngine();

        // Small workflow with known diagnostics
        var yaml1 = Encoding.UTF8.GetBytes("on: push\npermissions: write-all\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n");
        var result1 = engine.Check(yaml1, ".github/workflows/ci.yml");

        await Assert.That(result1.DiagnosticCount).IsEqualTo(result1.Diagnostics.Length);
        await Assert.That(result1.DiagnosticCount).IsGreaterThan(0);
        for (var i = 0; i < result1.Diagnostics.Length; i++)
        {
            await Assert.That(result1.Diagnostics[i].Message).IsNotNull();
        }

        // Larger workflow → more diagnostics
        var yaml2 = Encoding.UTF8.GetBytes("on: push\npermissions: write-all\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n      - run: echo ok\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n      - run: echo test\n");
        var result2 = engine.Check(yaml2, ".github/workflows/ci.yml");
        await Assert.That(result2.DiagnosticCount).IsEqualTo(result2.Diagnostics.Length);

        // Smaller workflow again → result array must shrink to exact size
        var yaml3 = Encoding.UTF8.GetBytes("on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n");
        var result3 = engine.Check(yaml3, ".github/workflows/ci.yml");
        await Assert.That(result3.DiagnosticCount).IsEqualTo(result3.Diagnostics.Length);
    }

    [Test]
    public async Task Check_FixableDiagnosticCount_ConsistentWithDiagnostics()
    {
        // FixableDiagnosticCount must count fix-bearing diagnostics correctly.
        var engine = new LintEngine();
        var yaml = Encoding.UTF8.GetBytes("on: push\npermissions: write-all\njobs:\n  build:\n    permissions:\n      contents: read\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n");

        var config = new LintConfig { Fix = new FixConfig { Enabled = true } };
        var result = engine.Check(yaml, ".github/workflows/ci.yml", config);

        await Assert.That(result.Diagnostics.Length).IsGreaterThan(0);

        var manualFixCount = 0;
        for (var i = 0; i < result.Diagnostics.Length; i++)
        {
            if (result.Diagnostics[i].Fix is not null)
            {
                manualFixCount++;
            }
        }
        await Assert.That(result.FixableDiagnosticCount).IsEqualTo(manualFixCount);
    }

    [Test]
    public async Task Check_WithRuleConfig_RepeatedCalls_ProducesCorrectResults()
    {
        // NormalizeRules dictionary reuse must not leak state between calls.
        var engine = new LintEngine();

        // Use unpinned-uses which is disableable and fires on actions/checkout@v4
        var yaml = Encoding.UTF8.GetBytes("on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n");

        // First call: disable unpinned-uses via config rules
        var configDisable = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new() { Enabled = false },
            },
        };
        var result1 = engine.Check(yaml, ".github/workflows/ci.yml", configDisable);
        var hasUnpinned1 = result1.Diagnostics.Any(d => d.RuleId == "unpinned-uses");

        // Second call: no rule config (unpinned-uses should fire)
        var result2 = engine.Check(yaml, ".github/workflows/ci.yml");
        var hasUnpinned2 = result2.Diagnostics.Any(d => d.RuleId == "unpinned-uses");

        // Third call: disable again
        var result3 = engine.Check(yaml, ".github/workflows/ci.yml", configDisable);
        var hasUnpinned3 = result3.Diagnostics.Any(d => d.RuleId == "unpinned-uses");

        await Assert.That(hasUnpinned1).IsFalse();
        await Assert.That(hasUnpinned2).IsTrue();
        await Assert.That(hasUnpinned3).IsFalse();
    }

    [Test]
    public async Task Check_WithInlineSuppression_RepeatedCalls_ProducesCorrectResults()
    {
        // ParseInlineSuppression dictionary reuse must not leak suppressions between calls.
        var engine = new LintEngine();

        // Source with inline suppression for unpinned-uses
        var yamlWithSuppression = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      # seiton: disable-next-line unpinned-uses\n      - uses: actions/checkout@v4\n");

        // Source without suppression
        var yamlWithout = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n");

        // First: suppressed → unpinned-uses should be suppressed
        var result1 = engine.Check(yamlWithSuppression, ".github/workflows/ci.yml");
        var suppressed1 = result1.SuppressionSummary.TotalSuppressed > 0
            && result1.SuppressionSummary.SuppressedByRule.ContainsKey("unpinned-uses");

        // Second: no suppression → unpinned-uses should appear
        var result2 = engine.Check(yamlWithout, ".github/workflows/ci.yml");
        var hasUnpinned2 = result2.Diagnostics.Any(d => d.RuleId == "unpinned-uses");

        // Third: suppressed again
        var result3 = engine.Check(yamlWithSuppression, ".github/workflows/ci.yml");
        var suppressed3 = result3.SuppressionSummary.TotalSuppressed > 0
            && result3.SuppressionSummary.SuppressedByRule.ContainsKey("unpinned-uses");

        await Assert.That(suppressed1).IsTrue();
        await Assert.That(hasUnpinned2).IsTrue();
        await Assert.That(suppressed3).IsTrue();
    }

    [Test]
    public async Task Check_WithRuleConfigAndSuppression_Combined_ProducesCorrectResults()
    {
        // Both NormalizeRules and ParseInlineSuppression reuse must work together.
        var engine = new LintEngine();

        // Source with file-level suppression for unpinned-uses
        var yamlFileSuppression = Encoding.UTF8.GetBytes(
            "# seiton: disable-file unpinned-uses\non: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n");

        // Source without suppression
        var yamlPlain = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n");

        // Call 1: file suppression + rule config disabling runner-label
        var config1 = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["runner-label"] = new() { Enabled = false },
            },
        };
        var result1 = engine.Check(yamlFileSuppression, ".github/workflows/ci.yml", config1);

        // Call 2: no suppression, no rule config
        var result2 = engine.Check(yamlPlain, ".github/workflows/ci.yml");

        // unpinned-uses should be suppressed in result1, present in result2
        var suppressed1 = result1.SuppressionSummary.SuppressedByRule.ContainsKey("unpinned-uses");
        var hasUnpinned2 = result2.Diagnostics.Any(d => d.RuleId == "unpinned-uses");

        await Assert.That(suppressed1).IsTrue();
        await Assert.That(hasUnpinned2).IsTrue();
    }

    [Test]
    public async Task Check_SkipSuppressionSummary_ReturnsSummaryEmpty()
    {
        // When SkipSuppressionSummary is true, suppression filtering still happens
        // (diagnostics are removed) but SuppressionSummary is Empty.
        var engine = new LintEngine();

        // Source with inline suppression for unpinned-uses
        var yaml = Encoding.UTF8.GetBytes(
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      # seiton: disable-next-line unpinned-uses\n      - uses: actions/checkout@v4\n");

        // Without skip: summary should have suppression data
        var resultFull = engine.Check(yaml, ".github/workflows/ci.yml");
        await Assert.That(resultFull.SuppressionSummary.TotalSuppressed).IsGreaterThan(0);

        // With skip: summary should be empty, but diagnostics should still be filtered
        var configSkip = new LintConfig { SkipSuppressionSummary = true };
        var resultSkip = engine.Check(yaml, ".github/workflows/ci.yml", configSkip);
        await Assert.That(resultSkip.SuppressionSummary.TotalSuppressed).IsEqualTo(0);
        await Assert.That(resultSkip.SuppressionSummary.Records).IsEmpty();

        // Suppressed diagnostics must NOT appear (suppression filtering still works)
        var hasUnpinned = resultSkip.Diagnostics.Any(d => d.RuleId == "unpinned-uses");
        await Assert.That(hasUnpinned).IsFalse();

        // Same diagnostic count as the full result (both filter the suppressed diagnostic)
        await Assert.That(resultSkip.Diagnostics.Length).IsEqualTo(resultFull.Diagnostics.Length);
    }
}
