namespace Seiton.Playground.Tests;

/// <summary>
/// Ensures Playwright static session teardown is safe to invoke repeatedly (assembly hook + process-exit fallback).
/// </summary>
[NotInParallel(PlaygroundUiTestHost.ParallelLockKey, Order = int.MinValue)]
public sealed class PlaygroundUiPlaywrightDisposalTests
{
    [Test]
    public async Task DisposePlaywrightSessionAsync_WhenCalledTwice_DoesNotThrow()
    {
        await PlaygroundUiLayoutTests.DisposePlaywrightSessionAsync();
        await PlaygroundUiLayoutTests.DisposePlaywrightSessionAsync();
    }
}
