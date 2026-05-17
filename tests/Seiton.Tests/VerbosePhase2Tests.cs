using Seiton.Cli;
using Seiton.Commands;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Tests;

public sealed class VerbosePhase2Tests
{
    // === Rule Summary Logging Tests ===

    [Test]
    public async Task WriteRuleSummary_EmitsEnabledAndDisabledCounts()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        CheckCommand.WriteRuleSummary(logger, activeRuleCount: 42, disabledRuleCount: 15, disabledRuleIds: []);

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("verbose: rules: 42 enabled, 15 disabled");
    }

    [Test]
    public async Task WriteRuleSummary_WithDisabledRuleIds_EmitsDisabledList()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        CheckCommand.WriteRuleSummary(logger, activeRuleCount: 40, disabledRuleCount: 3,
            disabledRuleIds: ["concurrency-limits", "impostor-commit", "ref-confusion"]);

        var lines = sw.ToString().TrimEnd().Split(Environment.NewLine);
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo("verbose: rules: 40 enabled, 3 disabled");
        await Assert.That(lines[1]).IsEqualTo("verbose: rules: disabled: concurrency-limits, impostor-commit, ref-confusion");
    }

    [Test]
    public async Task WriteRuleSummary_NoDisabledRules_OmitsDisabledLine()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        CheckCommand.WriteRuleSummary(logger, activeRuleCount: 57, disabledRuleCount: 0, disabledRuleIds: []);

        var output = sw.ToString().TrimEnd();
        await Assert.That(output).IsEqualTo("verbose: rules: 57 enabled, 0 disabled");
        await Assert.That(output).DoesNotContain("disabled:");
    }

    [Test]
    public async Task WriteRuleSummary_VerboseDisabled_EmitsNothing()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: false, sw);

        CheckCommand.WriteRuleSummary(logger, activeRuleCount: 42, disabledRuleCount: 15,
            disabledRuleIds: ["rule-a"]);

        await Assert.That(sw.ToString()).IsEqualTo("");
    }

    // === Document Kind Logging Tests ===

    [Test]
    public async Task LogFile_DocumentKind_Workflow()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        logger.LogFile(".github/workflows/ci.yml", "workflow");

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("verbose: .github/workflows/ci.yml: workflow");
    }

    [Test]
    public async Task LogFile_DocumentKind_ActionMetadata()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        logger.LogFile("action.yml", "action");

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("verbose: action.yml: action");
    }
}
