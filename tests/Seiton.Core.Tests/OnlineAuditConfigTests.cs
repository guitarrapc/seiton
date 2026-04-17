using Seiton.Core.Linting;
using Seiton.Core.Linting.OnlineAudit;

namespace Seiton.Core.Tests;

public sealed class OnlineAuditConfigTests
{
    [Test]
    public async Task OnlineAuditConfig_DefaultValues()
    {
        var config = new OnlineAuditConfig();

        await Assert.That(config.AllowNetwork).IsFalse();
        await Assert.That(config.FailOpen).IsTrue();
        await Assert.That(config.RequestTimeoutSec).IsEqualTo(30);
        await Assert.That(config.MaxConcurrency).IsEqualTo(4);
    }

    [Test]
    public async Task OnlineAuditGitHubConfig_DefaultValues()
    {
        var config = new OnlineAuditGitHubConfig();

        await Assert.That(config.TokenEnvVars).IsEquivalentTo(["SEITON_GITHUB_TOKEN", "GITHUB_TOKEN"]);
        await Assert.That(config.GhesApiUrl).IsNull();
        await Assert.That(config.GhesFallback).IsFalse();
        await Assert.That(config.IgnoreActions).IsEmpty();
    }

    [Test]
    public async Task LintConfig_OnlineAudit_DefaultIsNull()
    {
        var config = LintConfig.Empty;

        await Assert.That(config.OnlineAudit).IsNull();
    }

    [Test]
    public async Task LintConfig_OnlineAudit_CanBeSet()
    {
        var onlineAudit = new OnlineAuditConfig { AllowNetwork = true };
        var config = new LintConfig { OnlineAudit = onlineAudit };

        await Assert.That(config.OnlineAudit).IsNotNull();
        await Assert.That(config.OnlineAudit!.AllowNetwork).IsTrue();
    }
}
