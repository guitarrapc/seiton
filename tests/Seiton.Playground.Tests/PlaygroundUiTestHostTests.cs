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

        // Extra grace: teardown retries inside the host, but OS handles can lag briefly on CI/Windows.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Directory.Exists(root) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var second = await PlaygroundUiTestHost.GetOrCreateAsync();
        await Assert.That(second.PublishRoot).IsNotEqualTo(root);
        await Assert.That(Directory.Exists(root)).IsFalse();

        await PlaygroundUiTestHost.ShutdownAsync();
    }
}
