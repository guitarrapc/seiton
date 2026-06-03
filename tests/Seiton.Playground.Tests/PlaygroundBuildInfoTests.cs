namespace Seiton.Playground.Tests;

[NotInParallel(PlaygroundTestParallelism.AssemblyLockKey)]
public sealed class PlaygroundBuildInfoTests
{
    [Test]
    public async Task SelectDisplayVersion_StripsSourceRevisionSuffix()
    {
        var v = PlaygroundBuildInfo.SelectDisplayVersion("2.1.0+deadbeef", null);
        await Assert.That(v).IsEqualTo("2.1.0");
    }

    [Test]
    public async Task SelectDisplayVersion_FallsBackToAssemblyVersion()
    {
        var v = PlaygroundBuildInfo.SelectDisplayVersion(null, "3.0.0.0");
        await Assert.That(v).IsEqualTo("3.0.0.0");
    }

    [Test]
    public async Task SelectDisplayVersion_UsesZeroWhenBothMissing()
    {
        var v = PlaygroundBuildInfo.SelectDisplayVersion(null, null);
        await Assert.That(v).IsEqualTo("0.0.0");
    }

    [Test]
    public async Task GetDisplayVersion_CurrentAssembly_IsNonEmptySemverLike()
    {
        var v = PlaygroundBuildInfo.GetDisplayVersion(typeof(PlaygroundBuildInfo).Assembly);
        await Assert.That(v.Length).IsGreaterThan(0);
        await Assert.That(char.IsDigit(v[0])).IsTrue();
    }
}
