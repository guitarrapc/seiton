using Seiton.Core.Linting;

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

        var anonymousDefinition = statuses.First(s => s.Rule.Id == "anonymous-definition");
        await Assert.That(anonymousDefinition.Enabled).IsFalse();
        await Assert.That(anonymousDefinition.Reason).IsEqualTo("opt-in (not configured)");

        var misfeature = statuses.First(s => s.Rule.Id == "misfeature");
        await Assert.That(misfeature.Enabled).IsFalse();
        await Assert.That(misfeature.Reason).IsEqualTo("opt-in (not configured)");
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
                ["anonymous-definition"] = new RuleConfig { Enabled = true },
                ["misfeature"] = new RuleConfig { Enabled = true },
            }
        };

        var statuses = RuleListResolver.Resolve(config);
        var concurrencyLimits = statuses.First(s => s.Rule.Id == "concurrency-limits");
        var anonymousDefinition = statuses.First(s => s.Rule.Id == "anonymous-definition");
        var misfeature = statuses.First(s => s.Rule.Id == "misfeature");

        await Assert.That(concurrencyLimits.Enabled).IsTrue();
        await Assert.That(concurrencyLimits.Reason).IsEqualTo("config (enabled)");
        await Assert.That(anonymousDefinition.Enabled).IsTrue();
        await Assert.That(anonymousDefinition.Reason).IsEqualTo("config (enabled)");
        await Assert.That(misfeature.Enabled).IsTrue();
        await Assert.That(misfeature.Reason).IsEqualTo("config (enabled)");
    }

    [Test]
    public async Task Resolve_DenyWriteAll_CanBeDisabledByConfig()
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

        await Assert.That(denyWriteAll.Enabled).IsFalse();
        await Assert.That(denyWriteAll.Reason).IsEqualTo("config (disabled)");
    }

    [Test]
    public async Task Resolve_SecurityCriticalAndStructuralRules_CanBeDisabledByConfig()
    {
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["deny-write-all"] = new RuleConfig { Enabled = false },
                ["deny-read-all"] = new RuleConfig { Enabled = false },
                ["job-structure"] = new RuleConfig { Enabled = false },
            }
        };

        var statuses = RuleListResolver.Resolve(config);
        var denyWriteAll = statuses.First(s => s.Rule.Id == "deny-write-all");
        var denyReadAll = statuses.First(s => s.Rule.Id == "deny-read-all");
        var jobStructure = statuses.First(s => s.Rule.Id == "job-structure");

        await Assert.That(denyWriteAll.Enabled).IsFalse();
        await Assert.That(denyWriteAll.Reason).IsEqualTo("config (disabled)");
        await Assert.That(denyReadAll.Enabled).IsFalse();
        await Assert.That(denyReadAll.Reason).IsEqualTo("config (disabled)");
        await Assert.That(jobStructure.Enabled).IsFalse();
        await Assert.That(jobStructure.Reason).IsEqualTo("config (disabled)");
    }

    [Test]
    public async Task Resolve_ReturnsAllRules()
    {
        var statuses = RuleListResolver.Resolve(null);

        await Assert.That(statuses.Count).IsEqualTo(63);
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

    [Test]
    public async Task Resolve_ConfigExplicitlyEnablesDefaultRule_ReasonIsDefault()
    {
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["template-injection"] = new RuleConfig { Enabled = true },
            }
        };

        var statuses = RuleListResolver.Resolve(config);
        var templateInjection = statuses.First(s => s.Rule.Id == "template-injection");

        await Assert.That(templateInjection.Enabled).IsTrue();
        await Assert.That(templateInjection.Reason).IsEqualTo("default");
    }
}
