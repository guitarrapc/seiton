using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Seiton.Playground.Tests;

/// <summary>
/// Browser-level layout checks against a locally published playground (real fingerprinted assets).
/// </summary>
[NotInParallel(PlaygroundUiTestHost.ParallelLockKey)]
public sealed class PlaygroundUiLayoutTests
{
    private static readonly Regex _localStylesheetHref = new(@"href=""style[^""]*\.css""", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex _mainScriptSrc = new(@"src=""main(\.[^""]+ )?\.js""", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    [Test]
    public async Task PublishedIndex_ResolvesStylesheetAndMainScript()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var html = await File.ReadAllTextAsync(Path.Combine(host.WwwRootPath, "index.html"));
        await Assert.That(html.Contains("#[.{fingerprint}]", StringComparison.Ordinal)).IsFalse();
        await Assert.That(_localStylesheetHref.IsMatch(html)).IsTrue();
        await Assert.That(_mainScriptSrc.IsMatch(html)).IsTrue();
        await Assert.That(html).Contains("<script type=\"importmap\">", StringComparison.Ordinal);

        await Assert.That(html.Contains(">Permalink</button>", StringComparison.Ordinal)).IsFalse();
        await Assert.That(html.Contains(">Check</button>", StringComparison.Ordinal)).IsFalse();

        // Structural checks (avoid pinning full SVG path d= — brittle on harmless icon tweaks).
        await Assert.That(Regex.IsMatch(html, @"id\s*=\s*""permalink-btn""[\s\S]*?<svg\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))).IsTrue();
        await Assert.That(html).Contains(
            "workflow YAML and config in URL hash",
            StringComparison.OrdinalIgnoreCase);
        await Assert.That(Regex.IsMatch(html, @"id\s*=\s*""fetch-btn""[\s\S]*?<svg\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))).IsTrue();
        await Assert.That(html).Contains(
            "Fetch and lint YAML — enter a URL first",
            StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"toast-stack\"", StringComparison.Ordinal);
    }

    internal static async Task GotoPlaygroundAndWaitForLinterGridAsync(IPage page, string url)
    {
        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 120_000,
        });

        await page.WaitForFunctionAsync(
            "() => { const el = document.querySelector('#linter'); return el !== null && getComputedStyle(el).display === 'grid'; }",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 90_000 });
    }

    /// <summary>
    /// URL input tests only need the lightweight client-side handlers from <c>main.js</c>.
    /// </summary>
    private static async Task WaitForUrlControlsReadyAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            "() => document.body?.dataset.urlControlsReady === 'true'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });
    }

    [Test]
    public async Task Fetch_InFlight_KeepsButtonAndUrlFieldDisabled_OnInputPulse()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var host = await PlaygroundUiTestHost.GetOrCreateAsync();
            var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 900, Height = 720 },
            });
            var page = await context.NewPageAsync();

            await page.RouteAsync(
                "**/seiton-fetch-stall.example.invalid/**",

                async route =>
                {
                    await release.Task.ConfigureAwait(false);
                    await route.FulfillAsync(new RouteFulfillOptions { Body = "# ok:\n", ContentType = "text/yaml", Status = 200 });
                });

            await GotoPlaygroundAndWaitForLinterGridAsync(page, host.BaseUrl);
            await WaitForUrlControlsReadyAsync(page);

            await page.FillAsync("#url-input", "https://seiton-fetch-stall.example.invalid/workflow.yml");

            await page.Locator("#fetch-btn").ClickAsync();

            await page.WaitForFunctionAsync(
                "() => document.querySelector(\"#fetch-btn\")?.disabled === true && document.querySelector(\"#url-input\")?.disabled === true");

            await page.EvaluateAsync(
                "() => { document.querySelector(\"#url-input\")?.dispatchEvent(new Event(\"input\", { bubbles: true })); }");

            await page.WaitForTimeoutAsync(200);

            await Assert.That(await page.Locator("#fetch-btn").IsDisabledAsync()).IsTrue();
            await Assert.That(await page.Locator("#url-input").EvaluateAsync<bool>("el => el.disabled")).IsTrue();
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Test]
    public async Task BrowserHook_RunLint_DiagnosticsHaveMeaningfulFields()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 900, Height = 720 },
        });
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");

        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.runLint === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        const string yaml = """
            on: push
            permissions: write-all
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """;

        var result = await page.EvaluateAsync<HookRunLintResult>(
            """
            (src) => globalThis.__SEITON_PLAYGROUND_TEST__.runLint(src, '.github/workflows/ci.yml')
            """,
            yaml);

        await Assert.That(result.Ok).IsTrue().Because(result.Error ?? "unknown");
        await Assert.That(result.InternalError).IsFalse();
        await Assert.That(result.Diagnostics?.Length ?? 0).IsGreaterThan(0);

        var diag = Array.Find(result.Diagnostics ?? [], d => string.Equals(d.RuleId, "deny-write-all", StringComparison.Ordinal));
        await Assert.That(diag is not null).IsTrue();
        await Assert.That(diag!.Line).IsGreaterThan(0);
        await Assert.That(diag.Column).IsGreaterThan(0);
        await Assert.That(string.IsNullOrWhiteSpace(diag.Message)).IsFalse();
        await Assert.That(string.IsNullOrWhiteSpace(diag.Severity)).IsFalse();
    }

    [Test]
    public async Task Toast_Escape_WithFocusOutsideStack_DismissesTopToast()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 900, Height = 720 },
        });
        var page = await context.NewPageAsync();

        await GotoPlaygroundAndWaitForLinterGridAsync(page, host.BaseUrl);
        await WaitForUrlControlsReadyAsync(page);

        // Enter on an invalid-but-filled URL shows an info toast; focus stays in #url-input.
        await page.Locator("#url-input").FillAsync("http://oops");
        await page.Locator("#url-input").FocusAsync();
        await page.Keyboard.PressAsync("Enter");

        var infoToast = page.Locator("#toast-stack .toast--info");
        await infoToast.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        await page.Locator("#editor > .CodeMirror").ClickAsync();
        await page.EvaluateAsync(
            "() => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");

        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('#toast-stack .toast--info').length === 0",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 5000 });

        await Assert.That(await infoToast.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task FetchUrl_SingleLabelHost_KeepsFetchButtonDisabled()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 900, Height = 720 },
        });
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, host.BaseUrl);
        await WaitForUrlControlsReadyAsync(page);

        await page.FillAsync("#url-input", "http://oops");
        await Assert.That(await page.Locator("#fetch-btn").IsDisabledAsync()).IsTrue();
    }

    [Test]
    public async Task FetchUrl_MultiLabelHttpsHost_EnablesFetchButton()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 900, Height = 720 },
        });
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, host.BaseUrl);
        await WaitForUrlControlsReadyAsync(page);

        await page.FillAsync("#url-input", "https://example.com/raw.yml");
        await Assert.That(await page.Locator("#fetch-btn").IsDisabledAsync()).IsFalse();
    }

    [Test]
    public async Task DiagnosticsTable_LongMessage_CanExpandAndCollapse()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 900, Height = 720 },
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 120_000,
        });

        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderDiagnostics === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => globalThis.__SEITON_PLAYGROUND_TEST__.renderDiagnostics([{
              line: 1,
              column: 1,
              severity: 'Error',
              ruleId: 'long-msg-test',
              message: 'A'.repeat(220),
            }])
            """);

        var toggle = page.Locator(".diag-message-toggle");
        await toggle.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
        await Assert.That(await toggle.CountAsync()).IsEqualTo(1);
        await Assert.That(await page.Locator(".diag-message--collapsed").CountAsync()).IsEqualTo(1);
        await Assert.That(await toggle.TextContentAsync()).IsEqualTo("Show more");

        await toggle.ClickAsync();
        await Assert.That(await page.Locator(".diag-message--collapsed").CountAsync()).IsEqualTo(0);
        await Assert.That(await toggle.TextContentAsync()).IsEqualTo("Show less");

        await toggle.ClickAsync();
        await Assert.That(await page.Locator(".diag-message--collapsed").CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task DiagnosticsTable_MediumMessage_NoExpandToggleWhenFullyVisible()
    {
        const string message =
            "on.push has unexpected key \"branch\" for \"push\" section. did you mean \"branches\"? "
            + "expected one of \"branches\", \"branches-ignore\", \"paths\", \"paths-ignore\", \"tags\", \"tags-ignore\"";

        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 900, Height = 720 },
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 120_000,
        });

        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderDiagnostics === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            (msg) => globalThis.__SEITON_PLAYGROUND_TEST__.renderDiagnostics([{
              line: 1,
              column: 1,
              severity: 'Error',
              ruleId: 'medium-msg-test',
              message: msg,
            }])
            """,
            message);

        await page.EvaluateAsync(
            "() => new Promise((r) => globalThis.requestAnimationFrame(() => globalThis.requestAnimationFrame(r)))");
        await Assert.That(await page.Locator(".diag-message-toggle").CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task DiagnosticsTable_RendersPositiveLineColumnAndMessage()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 900, Height = 720 },
        });
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, host.BaseUrl);

        const string yaml = """
            on: push
            permissions: write-all
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """;

        await page.EvaluateAsync(
            """
            (src) => {
              const cm = document.querySelector('#editor .CodeMirror')?.CodeMirror;
              if (!cm) throw new Error('workflow editor missing');
              cm.setValue(src);
              cm.refresh();
            }
            """,
            yaml);

        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('#lint-result-body tr').length > 0",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 10_000 });

        var positionText = ((await page.Locator("#lint-result-body tr:first-child td:first-child .pos-chip").TextContentAsync()) ?? string.Empty).Trim();
        var severityText = ((await page.Locator("#lint-result-body tr:first-child td:nth-child(2) .severity-chip").TextContentAsync()) ?? string.Empty).Trim();
        var messageText = ((await page.Locator("#lint-result-body tr:first-child td:nth-child(3)").TextContentAsync()) ?? string.Empty).Trim();

        await Assert.That(Regex.IsMatch(positionText, @"^line:[1-9]\d*, col:[1-9]\d*$")).IsTrue();
        await Assert.That(new HashSet<string> { "Error", "Warning", "Info" }.Contains(severityText)).IsTrue();
        await Assert.That(string.IsNullOrWhiteSpace(messageText)).IsFalse();
    }

    [Test]
    public async Task Layout_WideViewport_UsesTwoColumnGridForLinterSection()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
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
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
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

    /// <summary>Forwarded to <see cref="PlaygroundUiBrowserSession"/> for assembly teardown tests.</summary>
    internal static Task DisposePlaywrightSessionAsync() => PlaygroundUiBrowserSession.DisposeAsync();

    private sealed class HookRunLintResult
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public bool InternalError { get; set; }
        public HookDiagnostic[]? Diagnostics { get; set; }
    }

    private sealed class HookDiagnostic
    {
        public string? Message { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string? Severity { get; set; }
        public string? RuleId { get; set; }
    }
}
