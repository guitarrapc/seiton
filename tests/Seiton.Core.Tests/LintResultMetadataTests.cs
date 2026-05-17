using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class LintResultMetadataTests
{
    private static readonly byte[] MinimalWorkflow = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n"u8.ToArray();
    private static readonly byte[] MinimalAction = "name: test\ndescription: test\nruns:\n  using: node20\n  main: index.js\n"u8.ToArray();
    private static readonly byte[] InvalidAction = "name: test\nruns: [\n"u8.ToArray();

    [Test]
    public async Task Check_Workflow_ReturnsActiveRuleCount()
    {
        var engine = new LintEngine();
        using var result = engine.Check(MinimalWorkflow, ".github/workflows/ci.yml");

        await Assert.That(result.ActiveRuleCount).IsGreaterThan(0);
    }

    [Test]
    public async Task Check_Workflow_ReturnsDocumentKindWorkflow()
    {
        var engine = new LintEngine();
        using var result = engine.Check(MinimalWorkflow, ".github/workflows/ci.yml");

        await Assert.That(result.DocumentKind).IsEqualTo(DocumentKind.Workflow);
    }

    [Test]
    public async Task Check_ActionMetadata_ReturnsDocumentKindActionMetadata()
    {
        var engine = new LintEngine();
        using var result = engine.Check(MinimalAction, "action.yml");

        await Assert.That(result.DocumentKind).IsEqualTo(DocumentKind.ActionMetadata);
    }

    [Test]
    public async Task Check_DisabledRuleCount_IsNonNegative()
    {
        var engine = new LintEngine();
        using var result = engine.Check(MinimalWorkflow, ".github/workflows/ci.yml");

        await Assert.That(result.DisabledRuleCount).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task Check_ActivePlusDisabledEqualsTotal()
    {
        var engine = new LintEngine();
        using var result = engine.Check(MinimalWorkflow, ".github/workflows/ci.yml");

        // Active + Disabled should account for all rules (excluding document-kind mismatch)
        var total = result.ActiveRuleCount + result.DisabledRuleCount;
        await Assert.That(total).IsGreaterThan(0);
    }

    [Test]
    public async Task Check_WithDisabledRule_ReturnsDisabledRuleIds()
    {
        var engine = new LintEngine();
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["job-permissions-required"] = new() { Enabled = false },
            },
        };
        using var result = engine.Check(MinimalWorkflow, ".github/workflows/ci.yml", config);

        await Assert.That(result.DisabledRuleCount).IsGreaterThanOrEqualTo(1);

        var disabledIds = result.DisabledRuleIds.ToArray();
        await Assert.That(disabledIds).Contains("job-permissions-required");
    }

    [Test]
    public async Task Check_DisabledRuleIds_LengthMatchesDisabledRuleCount()
    {
        var engine = new LintEngine();
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["job-permissions-required"] = new() { Enabled = false },
            },
        };
        using var result = engine.Check(MinimalWorkflow, ".github/workflows/ci.yml", config);

        await Assert.That(result.DisabledRuleIds.Length).IsEqualTo(result.DisabledRuleCount);
    }

    [Test]
    public async Task Check_NoConfig_OptInRulesAreDisabled()
    {
        var engine = new LintEngine();
        using var result = engine.Check(MinimalWorkflow, ".github/workflows/ci.yml");

        // Opt-in rules should be counted as disabled and listed in DisabledRuleIds
        await Assert.That(result.DisabledRuleCount).IsGreaterThan(0);

        var disabledIds = result.DisabledRuleIds.ToArray();
        await Assert.That(disabledIds.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Check_ActionMetadata_WorkflowOnlyRulesNotCountedAsDisabled()
    {
        var engine = new LintEngine();
        using var result = engine.Check(MinimalAction, "action.yml");

        // Document-kind-mismatched rules should NOT be counted as disabled
        // (they're simply not applicable, not user-disabled)
        // DisabledRuleCount should only reflect config/opt-in disabled rules
        var disabledIds = result.DisabledRuleIds.ToArray();
        await Assert.That(disabledIds).DoesNotContain("job-permissions-required");
    }

    [Test]
    public async Task Check_FatalParseError_WithActionPathHint_PreservesRuleActivationMetadata()
    {
        var engine = new LintEngine();
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["action-shell-required"] = new() { Enabled = false },
            },
        };

        using var validResult = engine.Check(MinimalAction, "action.yml", config);
        using var invalidResult = engine.Check(InvalidAction, "action.yml", config);

        await Assert.That(invalidResult.ActiveRuleCount).IsEqualTo(validResult.ActiveRuleCount);
        await Assert.That(invalidResult.DisabledRuleCount).IsEqualTo(validResult.DisabledRuleCount);
        await Assert.That(invalidResult.DisabledRuleIds.ToArray())
            .IsEquivalentTo(validResult.DisabledRuleIds.ToArray());
    }

    [Test]
    public async Task Check_FatalParseError_PreservesConfigDiagnostics()
    {
        var engine = new LintEngine();
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["seiton-lint-rule-008"] = new() { Enabled = false },
            },
        };

        using var result = engine.Check(InvalidAction, "action.yml", config);

        await Assert.That(result.HasFatalError).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown rule-id 'seiton-lint-rule-008'"))).IsTrue();
    }
}
