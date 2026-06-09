using Microsoft.Playwright;

namespace Seiton.Playground.Tests;

/// <summary>
/// Shared headless Chromium session for Playground UI tests (layout, share restore, etc.).
/// Serialized via <see cref="Gate"/> and <see cref="PlaygroundUiTestHost.ParallelLockKey"/> on test classes.
/// </summary>
internal static class PlaygroundUiBrowserSession
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IPlaywright? s_playwright;
    private static IBrowser? s_browser;

    static PlaygroundUiBrowserSession()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                TryDisposeOnProcessExit();
            }
            catch
            {
                // best effort
            }
        };
    }

    internal static async Task<IBrowser> GetBrowserAsync()
    {
        if (s_browser is { IsConnected: true })
        {
            return s_browser;
        }

        await Gate.WaitAsync();
        try
        {
            if (s_browser is { IsConnected: true })
            {
                return s_browser;
            }

            await DisposeBrowserLockedAsync();

            IPlaywright? playwrightLocal = null;
            try
            {
                playwrightLocal = await Playwright.CreateAsync();
                var browser = await playwrightLocal.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions { Headless = true });
                s_playwright = playwrightLocal;
                s_browser = browser;
                playwrightLocal = null;
                return browser;
            }
            finally
            {
                if (playwrightLocal is not null)
                {
                    try
                    {
                        playwrightLocal.Dispose();
                    }
                    catch
                    {
                        // best effort — launch failed after CreateAsync
                    }
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static async Task DisposeAsync()
    {
        await Gate.WaitAsync();
        try
        {
            await DisposeBrowserLockedAsync();

            if (s_playwright is not null)
            {
                s_playwright.Dispose();
                s_playwright = null;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Process exit must not block on <see cref="Gate"/> if another thread is in <see cref="GetBrowserAsync"/>.
    /// </summary>
    private static void TryDisposeOnProcessExit()
    {
        if (!Gate.Wait(TimeSpan.FromSeconds(1)))
        {
            return;
        }

        IBrowser? browser;
        IPlaywright? playwright;
        try
        {
            browser = s_browser;
            playwright = s_playwright;
            s_browser = null;
            s_playwright = null;
        }
        finally
        {
            Gate.Release();
        }

        if (browser is not null)
        {
            try
            {
                if (browser.IsConnected)
                {
                    browser.CloseAsync().GetAwaiter().GetResult();
                }
            }
            catch
            {
                // driver may already be closing
            }

            try
            {
                browser.DisposeAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // best effort
            }
        }

        if (playwright is not null)
        {
            try
            {
                playwright.Dispose();
            }
            catch
            {
                // best effort
            }
        }
    }

    private static async Task DisposeBrowserLockedAsync()
    {
        if (s_browser is null)
        {
            return;
        }

        try
        {
            if (s_browser.IsConnected)
            {
                await s_browser.CloseAsync();
            }
        }
        catch
        {
            // ignore — driver may already be closing
        }

        try
        {
            await s_browser.DisposeAsync();
        }
        catch
        {
            // best effort
        }

        s_browser = null;
    }
}
