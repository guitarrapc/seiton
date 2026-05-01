using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class OnlineAuditConfigTests
{
    [Test]
    public async Task NetworkConfig_DefaultValues()
    {
        var config = new NetworkConfig();

        await Assert.That(config.OnError).IsEqualTo(NetworkErrorMode.Skip);
        await Assert.That(config.TimeoutSeconds).IsEqualTo(30);
        await Assert.That(config.MaxConcurrency).IsEqualTo(LintConfigResourceLimits.DefaultNetworkMaxConcurrency);
    }

    [Test]
    public async Task GitHubNetworkConfig_DefaultValues()
    {
        var config = new GitHubNetworkConfig();

        await Assert.That(config.GhesApiUrl).IsNull();
        await Assert.That(config.GhesFallback).IsEqualTo(false);
    }

    [Test]
    public async Task LintConfig_Network_Default()
    {
        var config = LintConfig.Empty;

        await Assert.That(config.Network).IsNotNull();
        await Assert.That(config.Network.OnError).IsEqualTo(NetworkErrorMode.Skip);
    }
}
