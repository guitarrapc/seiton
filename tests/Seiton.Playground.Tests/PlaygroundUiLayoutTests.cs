using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Seiton.Playground.Tests;

/// <summary>
/// Browser-level layout checks against a locally published playground (real fingerprinted assets).
/// </summary>
public sealed class PlaygroundUiLayoutTests
{
    private static readonly Regex s_localStylesheetHref = new(@"href=""style[^""]*\.css""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex s_fingerprintedMainScriptSrc = new(@"src=""main\.[^""]+\.js""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly SemaphoreSlim s_browserGate = new(1, 1);
    private static IPlaywright? s_playwright;
    private static IBrowser? s_browser;

    private static async Task<IBrowser> GetBrowserAsync()
    {
        if (s_browser is not null)
        {
            return s_browser;
        }

        await s_browserGate.WaitAsync();
        try
        {
            if (s_browser is not null)
            {
                return s_browser;
            }

            s_playwright = await Playwright.CreateAsync();
            s_browser = await s_playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            return s_browser;
        }
        finally
        {
            s_browserGate.Release();
        }
    }

    [Test]
    public async Task PublishedIndex_ResolvesStylesheetAndFingerprintedMain()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var html = await File.ReadAllTextAsync(Path.Combine(host.WwwRootPath, "index.html"));
        await Assert.That(html.Contains("#[.{fingerprint}]", StringComparison.Ordinal)).IsFalse();
        await Assert.That(s_localStylesheetHref.IsMatch(html)).IsTrue();
        await Assert.That(s_fingerprintedMainScriptSrc.IsMatch(html)).IsTrue();
        var hasImportMapEntry = html.Contains("\"./main.js\": \"./main.", StringComparison.Ordinal)
            || html.Contains("\"./main.js\":\"./main.", StringComparison.Ordinal);
        await Assert.That(hasImportMapEntry).IsTrue();
    }

    private static async Task GotoPlaygroundAndWaitForLinterGridAsync(IPage page, string baseUrl)
    {
        await page.GotoAsync(baseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 120_000,
        });

        await page.WaitForFunctionAsync(
            "() => { const el = document.querySelector('#linter'); return el !== null && getComputedStyle(el).display === 'grid'; }",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 90_000 });
    }

    [Test]
    public async Task Layout_WideViewport_UsesTwoColumnGridForLinterSection()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
        });
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, host.BaseUrl);

        var display = await page.Locator("#linter").EvaluateAsync<string>("e => getComputedStyle(e).display");
        await Assert.That(display).IsEqualTo("grid");

        var linterBox = await page.Locator("#linter").BoundingBoxAsync();
        var editorBox = await page.Locator("#editor-wrap").BoundingBoxAsync();
        var resultsBox = await page.Locator(".results-column").BoundingBoxAsync();
        await Assert.That(linterBox).IsNotNull();
        await Assert.That(editorBox).IsNotNull();
        await Assert.That(resultsBox).IsNotNull();

        // Wide: two columns — results start to the right of the editor (robust vs minmax() in computed grid-template-columns).
        await Assert.That((double)resultsBox!.X).IsGreaterThan((double)editorBox!.X + editorBox.Width * 0.2);
        await Assert.That((double)Math.Abs(editorBox.Y - resultsBox!.Y)).IsLessThanOrEqualTo(8.0);
        await Assert.That(editorBox.Width).IsGreaterThan(100);
        await Assert.That(resultsBox.Width).IsGreaterThan(100);
    }

    [Test]
    public async Task Layout_NarrowViewport_StacksLinterColumns()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 600, Height = 720 },
        });
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, host.BaseUrl);

        var editorBox = await page.Locator("#editor-wrap").BoundingBoxAsync();
        var resultsBox = await page.Locator(".results-column").BoundingBoxAsync();
        await Assert.That(editorBox).IsNotNull();
        await Assert.That(resultsBox).IsNotNull();

        // Narrow: single column — results stack below the editor (avoids parsing grid-template-columns).
        var minDrop = Math.Max(40.0, editorBox!.Height * 0.25);
        await Assert.That((double)resultsBox!.Y).IsGreaterThan((double)editorBox.Y + minDrop);
        await Assert.That((double)Math.Abs(editorBox.X - resultsBox.X)).IsLessThanOrEqualTo(24.0);
    }
}
