using TUnit.Core;

namespace Seiton.Playground.Tests;

/// <summary>
/// Ensures published playground host and Playwright browser are released after this assembly finishes.
/// Playwright also registers <see cref="AppDomain.ProcessExit"/> in <see cref="PlaygroundUiLayoutTests"/> as a fallback.
/// </summary>
public static class PlaygroundUiTestAssemblyHooks
{
    [After(Assembly)]
    public static async Task TeardownPlaygroundUiSessionAsync()
    {
        await PlaygroundUiLayoutTests.DisposePlaywrightSessionAsync();
        await PlaygroundUiTestHost.ShutdownAsync();
    }
}
