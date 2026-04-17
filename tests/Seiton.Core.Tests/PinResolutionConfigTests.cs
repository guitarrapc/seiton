using Seiton.Core.Linting;
using Seiton.Core.Linting.PinRemediation;

namespace Seiton.Core.Tests;

public sealed class PinResolutionConfigTests
{
    [Test]
    public async Task PinResolutionConfig_DefaultValues()
    {
        var config = new PinResolutionConfig();

        await Assert.That(config.AllowNetwork).IsEqualTo(false);
        await Assert.That(config.FailOpen).IsEqualTo(true);
        await Assert.That(config.RequestTimeoutSec).IsEqualTo(30);
        await Assert.That(config.MaxConcurrency).IsEqualTo(4);
    }

    [Test]
    public async Task GitHubActionsResolutionConfig_DefaultValues()
    {
        var config = new GitHubActionsResolutionConfig();

        await Assert.That(config.TokenEnvVars).IsEquivalentTo(["SEITON_GITHUB_TOKEN", "GITHUB_TOKEN"]);
        await Assert.That(config.GhesApiUrl).IsNull();
        await Assert.That(config.GhesFallback).IsEqualTo(false);
        await Assert.That(config.IgnoreActions).IsEmpty();
        await Assert.That(config.ExcludeBranches).IsEquivalentTo(["main", "master"]);
        await Assert.That(config.MinAgeDays).IsEqualTo(14);
    }

    [Test]
    public async Task ImageResolutionConfig_DefaultValues()
    {
        var config = new ImageResolutionConfig();

        await Assert.That(config.ExcludeImages).Contains("scratch");
        await Assert.That(config.ExcludeTags).IsEquivalentTo(["latest"]);
        await Assert.That(config.IgnoreImages).IsEmpty();
    }

    [Test]
    public async Task ImageResolutionConfig_ScratchAlwaysEnforced_WhenOmitted()
    {
        var config = new ImageResolutionConfig
        {
            ExcludeImages = ["ubuntu"],
        };

        await Assert.That(config.ExcludeImages).Contains("scratch");
        await Assert.That(config.ExcludeImages).Contains("ubuntu");
    }

    [Test]
    public async Task ImageResolutionConfig_ScratchAlreadyPresent_NoduplicateAdded()
    {
        var config = new ImageResolutionConfig
        {
            ExcludeImages = ["scratch", "ubuntu"],
        };

        var scratchCount = config.ExcludeImages.Count(x => x == "scratch");
        await Assert.That(scratchCount).IsEqualTo(1);
    }

    [Test]
    public async Task LintConfig_PinResolution_DefaultIsNull()
    {
        var config = LintConfig.Empty;

        await Assert.That(config.PinResolution).IsNull();
    }

    [Test]
    public async Task LintConfig_PinResolution_CanBeSet()
    {
        var pinConfig = new PinResolutionConfig { AllowNetwork = true };
        var config = new LintConfig { PinResolution = pinConfig };

        await Assert.That(config.PinResolution).IsNotNull();
        await Assert.That(config.PinResolution!.AllowNetwork).IsEqualTo(true);
    }

    [Test]
    public async Task IgnoreActionEntry_StoresNameAndRefPattern()
    {
        var entry = new IgnoreActionEntry(
            @"slsa-framework/slsa-github-generator/.*",
            @".*");

        await Assert.That(entry.NamePattern).IsEqualTo(@"slsa-framework/slsa-github-generator/.*");
        await Assert.That(entry.RefPattern).IsEqualTo(@".*");
    }
}
