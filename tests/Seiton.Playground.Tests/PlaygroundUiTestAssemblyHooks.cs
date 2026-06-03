using Seiton.Playground;

namespace Seiton.Playground.Tests;

/// <summary>
/// Ensures published playground host and Playwright browser are released after this assembly finishes.
/// Playwright also registers <see cref="AppDomain.ProcessExit"/> in <see cref="PlaygroundUiBrowserSession"/> as a fallback.
/// </summary>
public static class PlaygroundUiTestAssemblyHooks
{
    [Before(Assembly)]
    public static void ResetPlaygroundSharedState()
    {
        PlaygroundLintRunner.ResetSharedStateForTests();
    }

    [After(Assembly)]
    public static async Task TeardownPlaygroundUiSessionAsync()
    {
        await PlaygroundUiBrowserSession.DisposeAsync();
        await PlaygroundUiTestHost.ShutdownAsync();
        PlaygroundLintRunner.ResetSharedStateForTests();
    }
}
