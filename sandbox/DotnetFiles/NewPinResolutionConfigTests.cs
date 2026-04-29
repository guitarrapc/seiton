using Seiton.Core.Linting;
using Seiton.Core.Linting.PinRemediation;

namespace Seiton.Core.Tests;

public sealed class PinResolutionConfigTests
{
    [Test]
    public async Task FixPinningConfig_DefaultValues()
    {
        var config = new FixPinningConfig();

        await Assert.That(config.EnableNetwork).IsEqualTo(false);
        await Assert.That(config.MinAgeDays).IsEqualTo(14);
        await Assert.That(config.ExcludeBranches).IsEquivalentTo(["main", "master"]);
        await Assert.That(config.IgnoreActions).IsEmpty();
    }

    [Test]
    public async Task FixImagesConfig_DefaultValues()
    {
        var config = new FixImagesConfig();

        await Assert.That(config.EnableNetwork).IsEqualTo(false);
        await Assert.That(config.ExcludeImages).Contains("scratch");
        await Assert.That(config.ExcludeTags).IsEquivalentTo(["latest"]);
        await Assert.That(config.IgnoreImages).IsEmpty();
    }

    [Test]
    public async Task FixImagesConfig_ScratchAlwaysEnforced_WhenOmitted()
    {
        var config = new FixImagesConfig
        {
            ExcludeImages = ["ubuntu"],
        };

        await Assert.That(config.ExcludeImages).Contains("scratch");
        await Assert.That(config.ExcludeImages).Contains("ubuntu");
    }

    [Test]
    public async Task FixImagesConfig_ScratchAlreadyPresent_NoDuplicateAdded()
    {
        var config = new FixImagesConfig
        {
            ExcludeImages = ["scratch", "ubuntu"],
        };

        var scratchCount = config.ExcludeImages.Count(x => x == "scratch");
        await Assert.That(scratchCount).IsEqualTo(1);
    }

    [Test]
    public async Task NetworkConfig_DefaultValues()
    {
        var config = new NetworkConfig();

        await Assert.That(config.OnError).IsEqualTo(NetworkErrorMode.Skip);
        await Assert.That(config.TimeoutSeconds).IsEqualTo(30);
        await Assert.That(config.MaxConcurrency).IsEqualTo(4);
    }

    [Test]
    public async Task GitHubNetworkConfig_DefaultValues()
    {
        var config = new GitHubNetworkConfig();

        await Assert.That(config.GhesApiUrl).IsNull();
        await Assert.That(config.GhesFallback).IsEqualTo(false);
    }

    [Test]
    public async Task LintConfig_Fix_Default()
    {
        var config = LintConfig.Empty;

        await Assert.That(config.Fix).IsNotNull();
        await Assert.That(config.Fix.Pinning.EnableNetwork).IsEqualTo(false);
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
