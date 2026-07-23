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

    [Test]
    public async Task MermaidOutput_EmptyAndJobForms_AreClassifiedCorrectly()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderMermaid === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        var emptyHiddenByInput = await page.EvaluateAsync<bool[]>(
            """
            () => {
              const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
              hooks.selectResultsTab('mermaid');
              return [
                `flowchart LR`,
                `flowchart LR
                  subgraph j0["build"]
                  end`,
                `flowchart LR
                  j0[["deploy — uses: octo/repo/.github/workflows/deploy.yml@v1"]]`,
                `flowchart LR
                  subgraph w0j0["build"]
                  end`,
                `flowchart LR
                  w0j0[["deploy — uses: octo/repo/.github/workflows/deploy.yml@v1"]]`,
              ].map((mermaid) => {
                hooks.renderMermaid(mermaid);
                return document.querySelector('#mermaid-empty').hidden;
              });
            }
            """);

        await Assert.That(emptyHiddenByInput).IsEquivalentTo([false, true, true, true, true]);
        await Assert.That(await page.Locator("#mermaid-empty").IsHiddenAsync()).IsTrue();
        await Assert.That(await page.Locator("#mermaid-output").IsVisibleAsync()).IsTrue();
        await Assert.That(await page.Locator("#mermaid-preview-btn").IsEnabledAsync()).IsTrue();
    }

    [Test]
    public async Task MermaidOutput_EmptyStringShowsEmptyStateAndDisablesToolbar()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderMermaid === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        var state = await page.EvaluateAsync<bool[]>(
            """
            () => {
              const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
              hooks.selectResultsTab('mermaid');
              hooks.renderMermaid('');
              return [
                document.querySelector('#mermaid-empty').hidden,
                document.querySelector('#mermaid-output').hidden,
                document.querySelector('#mermaid-preview-btn').disabled,
                document.querySelector('#mermaid-copy-btn').disabled,
              ];
            }
            """);

        await Assert.That(state).IsEquivalentTo([false, true, true, true]);
    }

    [Test]
    public async Task MermaidPreview_PansAndZoomsWithoutShrinkingBelowFit()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.setMermaidPreviewMode === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
              hooks.selectResultsTab('mermaid');
              hooks.renderMermaid(`flowchart LR
                a["checkout"] --> b["build"] --> c["test"] --> d["deploy"]`);
              hooks.setMermaidPreviewMode(true);
            }
            """);

        var viewport = page.Locator("#mermaid-preview .mermaid-preview__viewport");
        await viewport.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        var initialTransform = await viewport.GetAttributeAsync("transform");
        await Assert.That(await page.Locator("#mermaid-zoom-out-btn").IsEnabledAsync()).IsTrue();

        // The initial transform is already fit-to-view, so zooming farther out is clamped.
        await page.Locator("#mermaid-zoom-out-btn").ClickAsync();
        await page.WaitForTimeoutAsync(250);
        await Assert.That(await viewport.GetAttributeAsync("transform")).IsEqualTo(initialTransform);

        await page.Locator("#mermaid-zoom-in-btn").ClickAsync();
        await page.WaitForTimeoutAsync(250);
        var zoomedTransform = await viewport.GetAttributeAsync("transform");
        await Assert.That(zoomedTransform).IsNotEqualTo(initialTransform);

        var box = await page.Locator("#mermaid-preview").BoundingBoxAsync();
        await Assert.That(box).IsNotNull();
        await page.Mouse.MoveAsync(box!.X + box.Width / 2, box.Y + box.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(box.X + box.Width / 2 + 40, box.Y + box.Height / 2 + 25);
        await page.Mouse.UpAsync();
        await Assert.That(await viewport.GetAttributeAsync("transform")).IsNotEqualTo(zoomedTransform);

        await page.Locator("#mermaid-zoom-reset-btn").ClickAsync();
        await Assert.That(await viewport.GetAttributeAsync("transform")).IsEqualTo(initialTransform);
    }

    [Test]
    public async Task MermaidPreview_Resize_RefitsWithoutJavaScriptError()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 900, Height = 700 },
        });
        var page = await context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.setMermaidPreviewMode === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
              hooks.selectResultsTab('mermaid');
              hooks.renderMermaid(`flowchart LR
                a[checkout] --> b[build] --> c[test]`);
              hooks.setMermaidPreviewMode(true);
            }
            """);

        await page.Locator("#mermaid-preview .mermaid-preview__viewport").WaitForAsync(
            new LocatorWaitForOptions { Timeout = 30_000 });
        await page.SetViewportSizeAsync(640, 700);
        await page.EvaluateAsync(
            "() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)))");

        await Assert.That(pageErrors).IsEmpty();
    }

    [Test]
    public async Task FlowGraph_ZoomControls_ZoomInOutAndResetToInitialView()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlow === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              globalThis.__SEITON_PLAYGROUND_TEST__.selectResultsTab('flow');
              globalThis.__SEITON_PLAYGROUND_TEST__.renderFlow({
                version: 1,
                workflows: [{
                  file: 'ci.yml',
                  events: ['push'],
                  jobs: [
                    { id: 'build', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [] },
                    { id: 'deploy', kind: 'job', needs: ['build'], reducedNeeds: ['build'], runsOn: [], steps: [] },
                  ],
                }],
              });
            }
            """);

        var viewport = page.Locator("#flow-graph .flow-viewport");
        var initialTransform = await viewport.GetAttributeAsync("transform");
        await Assert.That(await page.Locator("#flow-zoom-in-btn").IsEnabledAsync()).IsTrue();

        await page.Locator("#flow-zoom-in-btn").ClickAsync();
        var zoomedInTransform = await viewport.GetAttributeAsync("transform");
        await Assert.That(zoomedInTransform).IsNotEqualTo(initialTransform);

        await page.Locator("#flow-zoom-reset-btn").ClickAsync();
        await Assert.That(await viewport.GetAttributeAsync("transform")).IsEqualTo(initialTransform);

        await page.Locator("#flow-zoom-out-btn").ClickAsync();
        await Assert.That(await viewport.GetAttributeAsync("transform")).IsNotEqualTo(initialTransform);

        await page.Locator("#flow-zoom-reset-btn").ClickAsync();
        await Assert.That(await viewport.GetAttributeAsync("transform")).IsEqualTo(initialTransform);

        await page.EvaluateAsync(
            "() => globalThis.__SEITON_PLAYGROUND_TEST__.renderFlow({ version: 1, workflows: [] })");
        await Assert.That(await page.Locator("#flow-zoom-out-btn").IsDisabledAsync()).IsTrue();
        await Assert.That(await page.Locator("#flow-zoom-reset-btn").IsDisabledAsync()).IsTrue();
        await Assert.That(await page.Locator("#flow-zoom-in-btn").IsDisabledAsync()).IsTrue();
    }

    [Test]
    public async Task FlowGraph_TouchPinch_ChangesScale()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            HasTouch = true,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
        });
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlow === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              globalThis.__SEITON_PLAYGROUND_TEST__.selectResultsTab('flow');
              globalThis.__SEITON_PLAYGROUND_TEST__.renderFlow({
                version: 1,
                workflows: [{
                  file: 'ci.yml',
                  events: ['push'],
                  jobs: [
                    { id: 'build', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [] },
                    { id: 'deploy', kind: 'job', needs: ['build'], reducedNeeds: ['build'], runsOn: [], steps: [] },
                  ],
                }],
              });
            }
            """);

        var scaleBefore = await page.EvaluateAsync<double>(
            "() => globalThis.d3.zoomTransform(document.querySelector('#flow-graph .flow-svg')).k");

        await page.EvaluateAsync(
            """
            () => {
              const svg = document.querySelector('#flow-graph .flow-svg');
              const rect = svg.getBoundingClientRect();
              const cx = rect.left + rect.width / 2;
              const cy = rect.top + rect.height / 2;
              const makeTouch = (id, x) => new Touch({
                identifier: id,
                target: svg,
                clientX: x,
                clientY: cy,
              });
              const touchEvent = (type, touches) => new TouchEvent(type, {
                bubbles: true,
                cancelable: true,
                touches,
                targetTouches: touches,
                changedTouches: touches,
              });
              const spread0 = 40;
              const spread1 = 80;
              const startTouches = [makeTouch(0, cx - spread0), makeTouch(1, cx + spread0)];
              svg.dispatchEvent(touchEvent('touchstart', startTouches));
              const moveTouches = [makeTouch(0, cx - spread1), makeTouch(1, cx + spread1)];
              svg.dispatchEvent(touchEvent('touchmove', moveTouches));
            }
            """);

        var scaleAfter = await page.EvaluateAsync<double>(
            "() => globalThis.d3.zoomTransform(document.querySelector('#flow-graph .flow-svg')).k");
        await Assert.That(scaleAfter).IsGreaterThan(scaleBefore);
    }

    [Test]
    public async Task FlowGraph_DiagnosticOnlyUpdate_RefreshesBadgeForFreshFlowObjects()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlowWithDiagnostics === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              const flow = () => ({
                version: 1,
                workflows: [{
                  file: 'ci.yml',
                  jobs: [{
                    id: 'build', kind: 'job', needs: [], reducedNeeds: [], runsOn: [],
                    line: 3, endLine: 6,
                    steps: [{ kind: 'run', id: 'test', line: 5, endLine: 5 }],
                  }],
                }],
              });
              const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
              hooks.selectResultsTab('flow');
              hooks.renderFlowWithDiagnostics(flow(), []);
              hooks.renderFlowWithDiagnostics(flow(), [{
                line: 5, column: 1, severity: 'Error', message: 'fresh diagnostic',
              }]);
            }
            """);

        await Assert.That(await page.Locator("#flow-graph .flow-job__diagbadge").TextContentAsync())
            .Contains("✖1");
        await Assert.That(await page.Locator("#flow-graph .flow-step .flow-marker").CountAsync())
            .IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task FlowGraph_PendingViewReset_SameStructure_ForcesRerender()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlowWithDiagnostics === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        var jobsVisible = await page.EvaluateAsync<bool>(
            """
            () => {
              const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
              hooks.selectResultsTab('flow');
              const flow = {
                version: 1,
                workflows: [{
                  file: 'ci.yml',
                  events: ['push'],
                  jobs: [
                    { id: 'build', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [
                      { id: 'checkout', kind: 'uses', line: 8, endLine: 8 },
                      { id: 'test', kind: 'run', line: 9, endLine: 9 },
                    ] },
                    { id: 'verify-updater', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [
                      { id: 'validate', kind: 'run', line: 20, endLine: 20 },
                    ] },
                  ],
                }],
              };
              hooks.renderFlowWithDiagnostics(flow, []);
              const svg = document.querySelector('#flow-graph .flow-svg');
              globalThis.d3.zoom().transform(
                globalThis.d3.select(svg),
                globalThis.d3.zoomIdentity.translate(-4000, -3000).scale(0.5),
              );
              hooks.resetFlowView();
              hooks.renderFlowWithDiagnostics(flow, []);
              const graph = document.querySelector('#flow-graph').getBoundingClientRect();
              const jobs = [...document.querySelectorAll('#flow-graph .flow-job')];
              if (jobs.length !== 2) return false;
              const tolerance = 4;
              return jobs.every((node) => {
                const rect = node.getBoundingClientRect();
                return rect.right >= graph.left - tolerance
                  && rect.left <= graph.right + tolerance
                  && rect.bottom >= graph.top - tolerance
                  && rect.top <= graph.bottom + tolerance;
              });
            }
            """);

        await Assert.That(jobsVisible).IsTrue();
    }

    [Test]
    public async Task FlowGraph_ZoomOut_AfterSmallMobileFit_DecreasesScale()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
        });
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlow === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              const jobs = Array.from({ length: 12 }, (_, index) => ({
                id: `job-${index}`,
                kind: 'job',
                needs: index === 0 ? [] : [`job-${index - 1}`],
                reducedNeeds: index === 0 ? [] : [`job-${index - 1}`],
                runsOn: [],
                steps: [],
              }));
              globalThis.__SEITON_PLAYGROUND_TEST__.selectResultsTab('flow');
              globalThis.__SEITON_PLAYGROUND_TEST__.renderFlow({
                version: 1,
                workflows: [{ file: 'ci.yml', events: ['push'], jobs }],
              });
            }
            """);

        var scaleBefore = await page.EvaluateAsync<double>(
            "() => globalThis.d3.zoomTransform(document.querySelector('#flow-graph .flow-svg')).k");
        await Assert.That(scaleBefore).IsLessThanOrEqualTo(0.5);

        // Zoom in until one toolbar zoom-out (×0.8) stays within the same LOD tier.
        for (var i = 0; i < 12; i++)
        {
            var safe = await page.EvaluateAsync<bool>(
                """
                () => {
                  const svg = document.querySelector('#flow-graph .flow-svg');
                  const k = globalThis.d3.zoomTransform(svg).k;
                  const lod = svg.classList.contains('flow-svg--lod2') ? 2
                    : svg.classList.contains('flow-svg--lod1') ? 1 : 0;
                  const dropK = lod === 2 ? 0.86 : lod === 1 ? 0.78 : null;
                  return dropK === null || k * 0.8 >= dropK + 0.01;
                }
                """);
            if (safe)
            {
                break;
            }
            await page.Locator("#flow-zoom-in-btn").ClickAsync();
        }

        scaleBefore = await page.EvaluateAsync<double>(
            "() => globalThis.d3.zoomTransform(document.querySelector('#flow-graph .flow-svg')).k");
        await Assert.That(scaleBefore).IsGreaterThan(0.5);

        await page.Locator("#flow-zoom-out-btn").ClickAsync();
        var scaleAfter = await page.EvaluateAsync<double>(
            "() => globalThis.d3.zoomTransform(document.querySelector('#flow-graph .flow-svg')).k");
        await Assert.That(scaleAfter).IsLessThan(scaleBefore);
    }

    [Test]
    public async Task FlowGraph_MobileResize_RefitsAndDragPansWholeGraph()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
        });
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlow === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              globalThis.__SEITON_PLAYGROUND_TEST__.selectResultsTab('flow');
              globalThis.__SEITON_PLAYGROUND_TEST__.renderFlow({
                version: 1,
                workflows: [{
                  file: 'ci.yml',
                  events: ['push'],
                  jobs: [
                    { id: 'a', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [] },
                    { id: 'b', kind: 'job', needs: ['a'], reducedNeeds: ['a'], runsOn: [], steps: [] },
                    { id: 'c', kind: 'job', needs: ['b'], reducedNeeds: ['b'], runsOn: [], steps: [] },
                    { id: 'd', kind: 'job', needs: ['c'], reducedNeeds: ['c'], runsOn: [], steps: [] },
                  ],
                }],
              });
            }
            """);

        await page.SetViewportSizeAsync(320, 700);
        await page.EvaluateAsync(
            "() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)))");

        var fits = await page.EvaluateAsync<bool>(
            """
            () => {
              const graph = document.querySelector('#flow-graph').getBoundingClientRect();
              const jobs = [...document.querySelectorAll('#flow-graph .flow-job')];
              if (jobs.length < 2) return false;
              const tolerance = 2;
              const graphOnScreen = graph.left >= -tolerance
                && graph.right <= globalThis.innerWidth + tolerance
                && graph.height <= globalThis.innerHeight;
              const jobsVisible = jobs.every((node) => {
                const rect = node.getBoundingClientRect();
                return rect.right >= graph.left - tolerance
                  && rect.left <= graph.right + tolerance
                  && rect.bottom >= graph.top - tolerance
                  && rect.top <= graph.bottom + tolerance;
              });
              return graphOnScreen && jobsVisible;
            }
            """);
        await Assert.That(fits).IsTrue();

        var before = await page.EvaluateAsync<FlowPanSnapshot>(
            """
            () => {
              const nodes = [...document.querySelectorAll('#flow-graph .flow-job')]
                .map((node) => node.getBoundingClientRect());
              return {
                transform: document.querySelector('#flow-graph .flow-viewport').getAttribute('transform'),
                firstToSecondX: nodes[1].x - nodes[0].x,
                firstToSecondY: nodes[1].y - nodes[0].y,
              };
            }
            """);
        var graph = page.Locator("#flow-graph");
        await graph.ScrollIntoViewIfNeededAsync();
        var graphBox = await graph.BoundingBoxAsync();
        await page.Mouse.MoveAsync(graphBox!.X + graphBox.Width / 2, graphBox.Y + graphBox.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(graphBox.X + graphBox.Width / 2 + 35, graphBox.Y + graphBox.Height / 2 + 20);
        await page.Mouse.UpAsync();

        var after = await page.EvaluateAsync<FlowPanSnapshot>(
            """
            () => {
              const nodes = [...document.querySelectorAll('#flow-graph .flow-job')]
                .map((node) => node.getBoundingClientRect());
              return {
                transform: document.querySelector('#flow-graph .flow-viewport').getAttribute('transform'),
                firstToSecondX: nodes[1].x - nodes[0].x,
                firstToSecondY: nodes[1].y - nodes[0].y,
              };
            }
            """);
        await Assert.That(after.Transform).IsNotEqualTo(before.Transform);
        await Assert.That(after.FirstToSecondX).IsEqualTo(before.FirstToSecondX).Within(0.01);
        await Assert.That(after.FirstToSecondY).IsEqualTo(before.FirstToSecondY).Within(0.01);
    }

    [Test]
    public async Task FlowGraph_MobileNodeSelection_HighlightsEditorWithoutPageJump()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
        });
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlow === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              globalThis.__SEITON_PLAYGROUND_TEST__.selectResultsTab('flow');
              globalThis.__SEITON_PLAYGROUND_TEST__.renderFlow({
                version: 1,
                workflows: [{
                  file: 'ci.yml',
                  events: ['push'],
                  jobs: [{
                    id: 'build',
                    kind: 'job',
                    line: 1,
                    endLine: 4,
                    needs: [],
                    reducedNeeds: [],
                    runsOn: [],
                    steps: [],
                  }],
                }],
              });
            }
            """);

        var graph = page.Locator("#flow-graph");
        await graph.ScrollIntoViewIfNeededAsync();
        var scrollBefore = await page.EvaluateAsync<double>("() => globalThis.scrollY");
        await page.Locator("#flow-graph .flow-job__header").ClickAsync();
        var scrollAfter = await page.EvaluateAsync<double>("() => globalThis.scrollY");

        await Assert.That(scrollAfter).IsEqualTo(scrollBefore).Within(1);
        await Assert.That(await page.Locator("#editor .flow-hl-line").CountAsync()).IsGreaterThan(0);
        await Assert.That(await page.Locator("#flow-detail").IsVisibleAsync()).IsTrue();
    }

    [Test]
    public async Task FlowGraph_NeedsLookup_IsCaseInsensitive()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlow === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => globalThis.__SEITON_PLAYGROUND_TEST__.renderFlow({
              version: 1,
              workflows: [{
                file: 'ci.yml',
                events: ['push'],
                jobs: [
                  { id: 'Build', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [] },
                  { id: 'deploy', kind: 'job', needs: ['build'], reducedNeeds: ['build'], runsOn: [], steps: [] },
                ],
              }],
            })
            """);

        await Assert.That(await page.Locator("#flow-graph .flow-edge").CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task FlowGraph_GroupEdge_HighlightsWithNeedsChain()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlow === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
                        () => globalThis.__SEITON_PLAYGROUND_TEST__.renderFlow({
                            version: 1,
                            workflows: [{
                                file: 'ci.yml',
                                events: ['push'],
                                jobs: [
                                    { id: 'build', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [] },
                                    { id: 'lint-a', kind: 'job', needs: ['build'], reducedNeeds: ['build'], runsOn: [], steps: [] },
                                    { id: 'lint-b', kind: 'job', needs: ['build'], reducedNeeds: ['build'], runsOn: [], steps: [] },
                                ],
                            }],
                        })
            """);
        await page.Locator("#flow-graph .flow-job").Nth(1).DispatchEventAsync("mouseenter");

        await Assert.That(
            await page.Locator("#flow-graph .flow-edge--group.flow-hover-related").CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task FlowGraph_GroupEdge_FollowsFrameAfterLodChange()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlow === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        var edgeEndsAtGroupFrame = await page.EvaluateAsync<bool>(
                    """
                        () => {
                            const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
                            hooks.selectResultsTab('flow');
                            hooks.renderFlow({
                                version: 1,
                                workflows: [{
                                    file: 'ci.yml',
                                    events: ['push'],
                                    jobs: [
                                        { id: 'build', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [] },
                                        { id: 'lint-a', kind: 'job', needs: ['build'], reducedNeeds: ['build'], runsOn: [], steps: [] },
                                        { id: 'lint-b', kind: 'job', needs: ['build'], reducedNeeds: ['build'], runsOn: [], steps: [] },
                                    ],
                                }],
                            });
                            const svg = document.querySelector('#flow-graph .flow-svg');
                            svg.dispatchEvent(new WheelEvent('wheel', {
                                bubbles: true,
                                cancelable: true,
                                clientX: 100,
                                clientY: 100,
                                deltaY: 1_000,
                            }));
                            const edge = document.querySelector('#flow-graph .flow-edge--group');
                            const frame = document.querySelector('#flow-graph .flow-needs-group');
                            const match = edge?.getAttribute('d')?.match(/([\d.-]+),([\d.-]+)$/);
                            if (!svg.classList.contains('flow-svg--lod0') || !frame || !match) return false;
                            const targetX = Number(match[1]);
                            const targetY = Number(match[2]);
                            const frameX = Number(frame.getAttribute('x'));
                            const frameY = Number(frame.getAttribute('y'));
                            const frameHeight = Number(frame.getAttribute('height'));
                            return Math.abs(targetX - frameX) < 0.01
                                && Math.abs(targetY - (frameY + frameHeight / 2)) < 0.01;
                        }
                        """);

        await Assert.That(edgeEndsAtGroupFrame).IsTrue();
    }

    [Test]
    public async Task FlowGraph_StructuralRerender_ResetsViewAfterPan()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlowWithDiagnostics === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        var jobsVisible = await page.EvaluateAsync<bool>(
            """
            () => {
              const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
              hooks.selectResultsTab('flow');
              hooks.renderFlowWithDiagnostics({
                version: 1,
                workflows: [{
                  file: 'a.yml',
                  events: ['push'],
                  jobs: [
                    { id: 'solo', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [{ id: 's1', kind: 'run', line: 5, endLine: 5 }] },
                  ],
                }],
              }, []);
              const svg = document.querySelector('#flow-graph .flow-svg');
              globalThis.d3.zoom().transform(globalThis.d3.select(svg), globalThis.d3.zoomIdentity.translate(-4000, -3000).scale(0.5));
              hooks.resetFlowView();
              hooks.renderFlowWithDiagnostics({
                version: 1,
                workflows: [{
                  file: 'b.yml',
                  events: ['push'],
                  jobs: [
                    { id: 'build', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [{ id: 'checkout', kind: 'uses', line: 8, endLine: 8 }] },
                    { id: 'verify-updater', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [{ id: 'validate', kind: 'run', line: 20, endLine: 20 }] },
                  ],
                }],
              }, []);
              const graph = document.querySelector('#flow-graph').getBoundingClientRect();
              const jobs = [...document.querySelectorAll('#flow-graph .flow-job')];
              if (jobs.length !== 2) return false;
              const tolerance = 4;
              return jobs.every((node) => {
                const rect = node.getBoundingClientRect();
                return rect.right >= graph.left - tolerance
                  && rect.left <= graph.right + tolerance
                  && rect.bottom >= graph.top - tolerance
                  && rect.top <= graph.bottom + tolerance;
              });
            }
            """);

        await Assert.That(jobsVisible).IsTrue();
    }

    [Test]
    public async Task FlowGraph_StructuralEdit_PreservesPanWhileTyping()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlowWithDiagnostics === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        var panPreserved = await page.EvaluateAsync<bool>(
            """
            () => {
              const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
              hooks.selectResultsTab('flow');
              const base = {
                version: 1,
                workflows: [{
                  file: 'ci.yml',
                  events: ['push'],
                  jobs: [{
                    id: 'build',
                    kind: 'job',
                    needs: [],
                    reducedNeeds: [],
                    runsOn: [],
                    steps: [{ id: 'checkout', kind: 'uses', line: 8, endLine: 8 }],
                  }],
                }],
              };
              hooks.renderFlowWithDiagnostics(base, []);
              const svg = document.querySelector('#flow-graph .flow-svg');
              const pan = globalThis.d3.zoomIdentity.translate(120, 80).scale(1.1);
              globalThis.d3.zoom().transform(globalThis.d3.select(svg), pan);
              const edited = structuredClone(base);
              edited.workflows[0].jobs.push({
                id: 'verify-updater',
                kind: 'job',
                needs: [],
                reducedNeeds: [],
                runsOn: [],
                steps: [{ id: 'validate', kind: 'run', line: 20, endLine: 20 }],
              });
              hooks.renderFlowWithDiagnostics(edited, []);
              const after = globalThis.d3.zoomTransform(svg);
              return Math.abs(after.x - pan.x) < 0.5
                && Math.abs(after.y - pan.y) < 0.5
                && Math.abs(after.k - pan.k) < 0.01;
            }
            """);

        await Assert.That(panPreserved).IsTrue();
    }

    [Test]
    public async Task FlowGraph_ZoomOutAtLod0_StaysAtLod0WithLimitedShrink()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlow === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
              hooks.selectResultsTab('flow');
              hooks.renderFlow({
                version: 1,
                workflows: [{
                  file: 'ci.yml',
                  events: ['push'],
                  jobs: [
                    { id: 'build', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [
                      { id: 'checkout', kind: 'uses', line: 8, endLine: 8 },
                      { id: 'test', kind: 'run', line: 9, endLine: 9 },
                    ] },
                    { id: 'lint-a', kind: 'job', needs: ['build'], reducedNeeds: ['build'], runsOn: [], steps: [
                      { id: 'lint', kind: 'run', line: 20, endLine: 20 },
                    ] },
                    { id: 'lint-b', kind: 'job', needs: ['build'], reducedNeeds: ['build'], runsOn: [], steps: [
                      { id: 'lint', kind: 'run', line: 30, endLine: 30 },
                    ] },
                  ],
                }],
              });
              const graph = document.querySelector('#flow-graph');
              const rect = graph.getBoundingClientRect();
              const cx = rect.left + rect.width / 2;
              const cy = rect.top + rect.height / 2;
              const svg = document.querySelector('#flow-graph .flow-svg');
              svg.dispatchEvent(new WheelEvent('wheel', {
                bubbles: true,
                cancelable: true,
                clientX: cx,
                clientY: cy,
                deltaY: 1_000,
              }));
            }
            """);

        await Assert.That(
            await page.EvaluateAsync<bool>(
                "() => document.querySelector('#flow-graph .flow-svg')?.classList.contains('flow-svg--lod0') ?? false"))
            .IsTrue();

        var kAtLod0 = await page.EvaluateAsync<double>(
            "() => globalThis.d3.zoomTransform(document.querySelector('#flow-graph .flow-svg')).k");

        await page.Locator("#flow-zoom-out-btn").ClickAsync();
        await page.Locator("#flow-zoom-out-btn").ClickAsync();
        await page.Locator("#flow-zoom-out-btn").ClickAsync();

        var staysLod0 = await page.EvaluateAsync<bool>(
            """
            () => {
              const svg = document.querySelector('#flow-graph .flow-svg');
              const kAfter = globalThis.d3.zoomTransform(svg).k;
              const graph = document.querySelector('#flow-graph').getBoundingClientRect();
              const jobs = [...document.querySelectorAll('#flow-graph .flow-job')];
              const tolerance = 6;
              const visibleCount = jobs.filter((node) => {
                const rect = node.getBoundingClientRect();
                return rect.right >= graph.left - tolerance
                  && rect.left <= graph.right + tolerance
                  && rect.bottom >= graph.top - tolerance
                  && rect.top <= graph.bottom + tolerance;
              }).length;
              return svg.classList.contains('flow-svg--lod0')
                && visibleCount >= 2;
            }
            """);

        await Assert.That(staysLod0).IsTrue();

        var kAfter = await page.EvaluateAsync<double>(
            "() => globalThis.d3.zoomTransform(document.querySelector('#flow-graph .flow-svg')).k");
        await Assert.That(kAfter).IsGreaterThanOrEqualTo(kAtLod0 * 0.92);
    }

    [Test]
    public async Task FlowGraph_Lod0RerenderThenZoomIn_ShowsStepFramesAfterMatrixEdit()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlowWithDiagnostics === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        var stepsVisible = await page.EvaluateAsync<string>(
            """
            () => {
              const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
              hooks.selectResultsTab('flow');
              const matrixCombo = (os) => ({ os });
              const flow = (combinations) => ({
                version: 1,
                workflows: [{
                  file: 'release.yaml',
                  events: ['push'],
                  jobs: [
                    {
                      id: 'validate',
                      kind: 'job',
                      needs: [],
                      reducedNeeds: [],
                      runsOn: [],
                      steps: [{ id: 'build', kind: 'run', line: 20, endLine: 20 }],
                    },
                    {
                      id: 'publish',
                      kind: 'job',
                      needs: ['validate'],
                      reducedNeeds: ['validate'],
                      runsOn: [],
                      strategy: { hasMatrix: true, combinations },
                      steps: [
                        { id: 'checkout', kind: 'uses', line: 40, endLine: 40 },
                        { id: 'publish', kind: 'run', line: 41, endLine: 41 },
                      ],
                    },
                  ],
                }],
              });
              const wheel = (deltaY, count) => {
                const graph = document.querySelector('#flow-graph');
                const rect = graph.getBoundingClientRect();
                const cx = rect.left + rect.width / 2;
                const cy = rect.top + rect.height / 2;
                const svg = document.querySelector('#flow-graph .flow-svg');
                for (let i = 0; i < count; i++) {
                  svg.dispatchEvent(new WheelEvent('wheel', {
                    bubbles: true,
                    cancelable: true,
                    clientX: cx,
                    clientY: cy,
                    deltaY,
                  }));
                }
                return svg;
              };
              hooks.renderFlowWithDiagnostics(flow([
                matrixCombo('linux-x64'),
                matrixCombo('linux-arm64'),
                matrixCombo('win-x64'),
              ]), []);
              wheel(1_000, 1);
              let svg = document.querySelector('#flow-graph .flow-svg');
              if (!svg?.classList.contains('flow-svg--lod0')) return 'not-lod0-initial';
              hooks.renderFlowWithDiagnostics(flow([
                matrixCombo('linux-x64'),
                matrixCombo('linux-arm64'),
              ]), []);
              svg = document.querySelector('#flow-graph .flow-svg');
              if (!svg?.classList.contains('flow-svg--lod0')) return 'not-lod0-after-edit';
              wheel(-1_000, 12);
              svg = document.querySelector('#flow-graph .flow-svg');
              const lod = svg.classList.contains('flow-svg--lod2') ? 2
                : svg.classList.contains('flow-svg--lod1') ? 1
                : 0;
              if (lod === 0) return 'lod0-after-zoom-in';
              const steps = [...document.querySelectorAll('#flow-graph .flow-step-node')];
              if (steps.length < 2) return 'few-steps';
              for (const inner of document.querySelectorAll('#flow-graph .flow-job__inner')) {
                const jobSteps = [...inner.querySelectorAll('.flow-step-node')];
                const ys = jobSteps.map((el) => Number(el.getAttribute('y')));
                if (new Set(ys).size !== ys.length) return 'overlap-within-job';
              }
              return 'ok';
            }
            """);

        await Assert.That(stepsVisible).IsEqualTo("ok");
    }

    [Test]
    public async Task FlowGraph_RepeatedWheelZoom_KeepsJobsOnScreen()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlow === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        var jobsVisible = await page.EvaluateAsync<bool>(
            """
            () => {
              const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
              hooks.selectResultsTab('flow');
              hooks.renderFlow({
                version: 1,
                workflows: [{
                  file: 'ci.yml',
                  events: ['push'],
                  jobs: [
                    { id: 'build', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [
                      { id: 'checkout', kind: 'uses', line: 8, endLine: 8 },
                      { id: 'test', kind: 'run', line: 9, endLine: 9 },
                    ] },
                    { id: 'verify-updater', kind: 'job', needs: [], reducedNeeds: [], runsOn: [], steps: [
                      { id: 'validate', kind: 'run', line: 20, endLine: 20 },
                    ] },
                  ],
                }],
              });
              const svg = document.querySelector('#flow-graph .flow-svg');
              for (let i = 0; i < 40; i++) {
                svg.dispatchEvent(new WheelEvent('wheel', {
                  bubbles: true,
                  cancelable: true,
                  clientX: 120,
                  clientY: 120,
                  deltaY: i % 2 === 0 ? 120 : -80,
                }));
              }
              const graph = document.querySelector('#flow-graph').getBoundingClientRect();
              const jobs = [...document.querySelectorAll('#flow-graph .flow-job')];
              if (jobs.length !== 2) return false;
              const tolerance = 6;
              return jobs.every((node) => {
                const rect = node.getBoundingClientRect();
                return rect.right >= graph.left - tolerance
                  && rect.left <= graph.right + tolerance
                  && rect.bottom >= graph.top - tolerance
                  && rect.top <= graph.bottom + tolerance;
              });
            }
            """);

        await Assert.That(jobsVisible).IsTrue();
    }

    [Test]
    public async Task FlowGraph_ToolbarZoomIn_AtLod0_SyncsLodWithStepsVisible()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.renderFlow === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              const hooks = globalThis.__SEITON_PLAYGROUND_TEST__;
              hooks.selectResultsTab('flow');
              hooks.renderFlow({
                version: 1,
                workflows: [{
                  file: 'ci.yml',
                  events: ['push'],
                  jobs: [{
                    id: 'build',
                    kind: 'job',
                    needs: [],
                    reducedNeeds: [],
                    runsOn: [],
                    steps: [
                      { id: 'checkout', kind: 'uses', line: 8, endLine: 8 },
                      { id: 'test', kind: 'run', line: 9, endLine: 9 },
                    ],
                  }],
                }],
              });
              const svg = document.querySelector('#flow-graph .flow-svg');
              for (let i = 0; i < 12; i++) {
                svg.dispatchEvent(new WheelEvent('wheel', {
                  bubbles: true,
                  cancelable: true,
                  clientX: 100,
                  clientY: 100,
                  deltaY: 200,
                }));
              }
            }
            """);

        await page.Locator("#flow-zoom-in-btn").ClickAsync();
        await page.Locator("#flow-zoom-in-btn").ClickAsync();
        await page.Locator("#flow-zoom-in-btn").ClickAsync();

        var lodSynced = await page.EvaluateAsync<bool>(
            """
            () => {
              const svg = document.querySelector('#flow-graph .flow-svg');
              const inner = document.querySelector('#flow-graph .flow-job__inner');
              if (!svg || !inner) return false;
              const lod0 = svg.classList.contains('flow-svg--lod0');
              const innerVisible = inner.getBoundingClientRect().width > 0;
              return !lod0 && innerVisible;
            }
            """);

        await Assert.That(lodSynced).IsTrue();
    }

    [Test]
    public async Task FlowTab_FailedRender_RetriesUnchangedSource()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.selectResultsTab === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              const cm = document.querySelector('#editor .CodeMirror').CodeMirror;
              cm.setValue('on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n');
              globalThis.__seitonSavedD3 = globalThis.d3;
              globalThis.d3 = undefined;
              globalThis.__SEITON_PLAYGROUND_TEST__.selectResultsTab('flow');
            }
            """);
        await Assert.That(await page.Locator("#flow-graph .flow-job").CountAsync()).IsEqualTo(0);

        await page.EvaluateAsync(
            """
            () => {
              globalThis.d3 = globalThis.__seitonSavedD3;
              globalThis.__SEITON_PLAYGROUND_TEST__.selectResultsTab('result');
              globalThis.__SEITON_PLAYGROUND_TEST__.selectResultsTab('flow');
            }
            """);

        await Assert.That(await page.Locator("#flow-graph .flow-job").CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task FlowTab_IncompleteUses_RendersCurrentPartialWorkflow()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl.TrimEnd('/')}/?seitonTestHooks=1");
        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.selectResultsTab === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await page.EvaluateAsync(
            """
            () => {
              const cm = document.querySelector('#editor .CodeMirror').CodeMirror;
              cm.setValue('on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n');
              globalThis.__SEITON_PLAYGROUND_TEST__.selectResultsTab('flow');
            }
            """);
        await Assert.That(await page.Locator("#flow-graph .flow-job").CountAsync()).IsEqualTo(1);

        await page.EvaluateAsync(
            """
            () => {
              const cm = document.querySelector('#editor .CodeMirror').CodeMirror;
              cm.setValue('on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses:');
            }
            """);
        await page.WaitForTimeoutAsync(700);

        await Assert.That(await page.Locator("#flow-graph .flow-job").CountAsync()).IsEqualTo(1);
        await Assert.That(await page.Locator("#flow-empty").IsVisibleAsync()).IsFalse();
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
    public async Task DiagnosticsTable_LongUrlMessage_NarrowViewport_ShowsExpandToggle()
    {
        const string message =
            "character '\\' is invalid for branch and tag names. only special characters [, ?, +, *, \\, ! can be escaped with \\. "
            + "see `man git-check-ref-format` for more details. note that regular expression is unavailable. "
            + "note: filter pattern syntax is explained at https://docs.github.com/en/actions/using-workflows/workflow-syntax-for-github-actions#filter-pattern-cheat-sheet";

        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 600, Height = 720 },
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
              severity: 'Warning',
              ruleId: 'long-url-msg-test',
              message: msg,
            }])
            """,
            message);

        var toggle = page.Locator(".diag-message-toggle");
        await toggle.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
        await Assert.That(await toggle.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task DiagnosticsTable_ShortMessage_NoExpandToggleWhenFullyVisible()
    {
        // Keep short enough to stay within three rendered lines in the results column (900px still uses a 2-column linter grid).
        const string message =
            "on.push has unexpected key \"branch\" for \"push\" section. did you mean \"branches\"?";

        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
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
              ruleId: 'short-msg-test',
              message: msg,
            }])
            """,
            message);

        await page.EvaluateAsync(
            "() => new Promise((r) => globalThis.requestAnimationFrame(() => globalThis.requestAnimationFrame(r)))");

        var renderedLines = await page.EvaluateAsync<int>(
            """
            () => {
              const msg = document.querySelector('.diag-message');
              if (!msg) return 0;
              const range = document.createRange();
              range.selectNodeContents(msg);
              const tops = new Set();
              for (const rect of range.getClientRects()) {
                tops.add(Math.round(rect.top));
              }
              return tops.size;
            }
            """);
        await Assert.That(renderedLines).IsLessThanOrEqualTo(3);
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

    private sealed class FlowPanSnapshot
    {
        public string? Transform { get; set; }
        public double FirstToSecondX { get; set; }
        public double FirstToSecondY { get; set; }
    }

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
