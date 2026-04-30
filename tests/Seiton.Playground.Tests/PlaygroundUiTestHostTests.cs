using TUnit.Core;

using TUnit.Core;

namespace Seiton.Playground.Tests;

/// <summary>
/// Regression: cached host must be disposable and temp publish dirs removed (see PlaygroundUiTestHost).
/// </summary>
public sealed class PlaygroundUiTestHostTests
{
    [Test]
    [NotInParallel(PlaygroundUiTestHost.ParallelLockKey, Order = int.MaxValue)]
    public async Task ShutdownAsync_AfterGetOrCreate_RemovesPublishRootAndAllowsNewHost()
    {
        var first = await PlaygroundUiTestHost.GetOrCreateAsync();
        var root = first.PublishRoot;
        await Assert.That(Directory.Exists(root)).IsTrue();

        await PlaygroundUiTestHost.ShutdownAsync();

        var second = await PlaygroundUiTestHost.GetOrCreateAsync();
        await Assert.That(second.PublishRoot).IsNotEqualTo(root);
        await Assert.That(Directory.Exists(root)).IsFalse();

        await PlaygroundUiTestHost.ShutdownAsync();
    }
}
