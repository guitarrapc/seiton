using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class RuleListResolverTests
{
    [Test]
    public async Task Resolve_NullConfig_AllDefaultRulesEnabled()
    {
        var statuses = RuleListResolver.Resolve(null);

        // All default-on rules should be enabled
        var jobStructure = statuses.First(s => s.Rule.Id == "job-structure");
        await Assert.That(jobStructure.Enabled).IsTrue();
        await Assert.That(jobStructure.Reason).IsEqualTo("default");
    }

    [Test]
    public async Task Resolve_NullConfig_OptInRulesDisabled()
    {
        var statuses = RuleListResolver.Resolve(null);

        var concurrencyLimits = statuses.First(s => s.Rule.Id == "concurrency-limits");
        await Assert.That(concurrencyLimits.Enabled).IsFalse();
        await Assert.That(concurrencyLimits.Reason).IsEqualTo("opt-in (not configured)");
    }

    [Test]
    public async Task Resolve_NullConfig_OnlineRulesDisabled()
    {
        var statuses = RuleListResolver.Resolve(null);

        var knownVuln = statuses.First(s => s.Rule.Id == "known-vulnerable-actions");
        await Assert.That(knownVuln.Enabled).IsFalse();
        await Assert.That(knownVuln.Reason).IsEqualTo("opt-in (not configured)");
    }

    [Test]
    public async Task Resolve_ConfigDisablesRule_MarkedDisabled()
    {
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["template-injection"] = new RuleConfig { Enabled = false },
            }
        };

        var statuses = RuleListResolver.Resolve(config);
        var templateInjection = statuses.First(s => s.Rule.Id == "template-injection");

        await Assert.That(templateInjection.Enabled).IsFalse();
        await Assert.That(templateInjection.Reason).IsEqualTo("config (disabled)");
    }

    [Test]
    public async Task Resolve_ConfigEnablesOptInRule_MarkedEnabled()
    {
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["concurrency-limits"] = new RuleConfig { Enabled = true },
            }
        };

        var statuses = RuleListResolver.Resolve(config);
        var concurrencyLimits = statuses.First(s => s.Rule.Id == "concurrency-limits");

        await Assert.That(concurrencyLimits.Enabled).IsTrue();
        await Assert.That(concurrencyLimits.Reason).IsEqualTo("config (enabled)");
    }

    [Test]
    public async Task Resolve_NonDisableableRule_CannotBeDisabled()
    {
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["deny-write-all"] = new RuleConfig { Enabled = false },
            }
        };

        var statuses = RuleListResolver.Resolve(config);
        var denyWriteAll = statuses.First(s => s.Rule.Id == "deny-write-all");

        await Assert.That(denyWriteAll.Enabled).IsTrue();
        await Assert.That(denyWriteAll.Reason).IsEqualTo("non-disableable");
    }

    [Test]
    public async Task Resolve_ReturnsAllRules()
    {
        var statuses = RuleListResolver.Resolve(null);

        await Assert.That(statuses.Count).IsEqualTo(56);
    }

    [Test]
    public async Task Resolve_EmptyConfig_SameAsNull()
    {
        var config = LintConfig.Empty;
        var statuses = RuleListResolver.Resolve(config);

        var jobStructure = statuses.First(s => s.Rule.Id == "job-structure");
        await Assert.That(jobStructure.Enabled).IsTrue();
        await Assert.That(jobStructure.Reason).IsEqualTo("default");
    }
}
