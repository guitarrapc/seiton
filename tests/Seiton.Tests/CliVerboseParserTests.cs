using Seiton.Cli;

namespace Seiton.Tests;

public sealed class CliVerboseParserTests
{
    [Test]
    public async Task Parse_NoFlags_ReturnsOff()
    {
        await Assert.That(CliVerboseParser.Parse(["check", "ci.yml"])).IsEqualTo(VerboseLevel.Off);
    }

    [Test]
    public async Task Parse_VFlag_ReturnsSummary()
    {
        await Assert.That(CliVerboseParser.Parse(["check", "-v", "ci.yml"])).IsEqualTo(VerboseLevel.Summary);
    }

    [Test]
    public async Task Parse_VVerboseFlag_ReturnsSummary()
    {
        await Assert.That(CliVerboseParser.Parse(["--verbose", "check"])).IsEqualTo(VerboseLevel.Summary);
    }

    [Test]
    public async Task Parse_VvFlag_ReturnsFiles()
    {
        await Assert.That(CliVerboseParser.Parse(["check", "-vv"])).IsEqualTo(VerboseLevel.Files);
    }

    [Test]
    public async Task Parse_VvAndV_ReturnsFiles()
    {
        await Assert.That(CliVerboseParser.Parse(["-v", "-vv"])).IsEqualTo(VerboseLevel.Files);
    }

    [Test]
    public async Task FilterArgsForFramework_RemovesVv()
    {
        var filtered = CliVerboseParser.FilterArgsForFramework(["check", "-vv", "ci.yml"]);
        await Assert.That(filtered.Length).IsEqualTo(2);
        await Assert.That(filtered[0]).IsEqualTo("check");
        await Assert.That(filtered[1]).IsEqualTo("ci.yml");
    }

    [Test]
    public async Task FilterArgsForFramework_PreservesArgsAfterEndOfOptions()
    {
        var filtered = CliVerboseParser.FilterArgsForFramework(["check", "--", "-vv"]);
        await Assert.That(filtered.Length).IsEqualTo(3);
        await Assert.That(filtered[0]).IsEqualTo("check");
        await Assert.That(filtered[1]).IsEqualTo("--");
        await Assert.That(filtered[2]).IsEqualTo("-vv");
    }

    [Test]
    public async Task FilterArgsForFramework_OnlyVvAfterEndOfOptions_ReturnsOriginalArray()
    {
        var args = new[] { "check", "--", "-vv" };
        var filtered = CliVerboseParser.FilterArgsForFramework(args);
        await Assert.That(ReferenceEquals(filtered, args)).IsTrue();
    }

    [Test]
    public async Task Resolve_FrameworkVerboseTrue_UpgradesToSummary()
    {
        await Assert.That(CliVerboseParser.Resolve(["check"], frameworkVerbose: true)).IsEqualTo(VerboseLevel.Summary);
    }
}
