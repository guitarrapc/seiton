namespace Seiton.Playground.Tests;

/// <summary>
/// Fast structural checks for the flow tab UI (no browser, no publish).
/// The flow tab renders the flow-json contract from <see cref="PlaygroundFlowRunner"/> as a D3 graph.
/// </summary>
[NotInParallel(PlaygroundTestParallelism.AssemblyLockKey)]
public sealed class PlaygroundFlowTabContractTests
{
    [Test]
    public async Task IndexTemplate_HasResultAndFlowTabLandmarks()
    {
        var html = await ReadWwwrootFileAsync("index.html");
        await Assert.That(html).Contains("id=\"results-tab-bar\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"tab-result-btn\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"tab-flow-btn\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"result-panel\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"flow-panel\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"flow-graph\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"flow-detail\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("role=\"tablist\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("aria-selected=\"true\"", StringComparison.Ordinal);

        // Existing result landmarks must stay stable (used by layout tests and main.js).
        await Assert.That(html).Contains("id=\"lint-result\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"success-msg\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("class=\"split-pane results-column\"", StringComparison.Ordinal);
    }

    [Test]
    public async Task IndexTemplate_D3CdnAssetHasSubresourceIntegrity()
    {
        var html = await ReadWwwrootFileAsync("index.html");
        await Assert.That(html).Contains("d3/7.9.0/d3.min.js", StringComparison.Ordinal);
        // SRI from https://api.cdnjs.com/libraries/d3/7.9.0 — recompute when bumping the D3 version.
        await Assert.That(html).Contains(
            "integrity=\"sha512-vc58qvvBdrDR4etbxMdlTt4GBQk1qjvyORR2nrsPsFPyrs+/u5c3+1Ct6upOgdZoIl7eq6k3a1UPDSNAQi/32A==\"",
            StringComparison.Ordinal);
    }

    [Test]
    public async Task MainJs_FlowTab_WiresInteropTabSwitchAndTestHooks()
    {
        var js = await ReadWwwrootFileAsync("main.js");
        await Assert.That(js).Contains("GetFlowJson");
        await Assert.That(js).Contains("refreshFlow");
        await Assert.That(js).Contains("selectResultsTab");
        await Assert.That(js).Contains("from './flow-graph.js'");
        await Assert.That(js).Contains("getFlow:");
    }

    [Test]
    public async Task FlowGraphModule_DefinesD3RendererWithZoomAndParallelBoundary()
    {
        var js = await ReadWwwrootFileAsync("flow-graph.js");
        await Assert.That(js).Contains("renderFlowGraph");
        await Assert.That(js).Contains("d3.zoom");
        await Assert.That(js).Contains("flow-edge");
        await Assert.That(js).Contains("flow-job");
        await Assert.That(js).Contains("flow-parallel-boundary");
    }

    [Test]
    public async Task FlowGraphModule_DefinesIntraJobFlowWithLod()
    {
        var js = await ReadWwwrootFileAsync("flow-graph.js");
        // Intra-job step flow: background steps fork off the main lane, wait/wait-all join them.
        await Assert.That(js).Contains("buildStepGraph");
        await Assert.That(js).Contains("flow-step-edge");
        await Assert.That(js).Contains("background");
        await Assert.That(js).Contains("wait-all");
        // Zoom-driven level of detail.
        await Assert.That(js).Contains("flow-svg--lod0");
        await Assert.That(js).Contains("flow-svg--lod2");
    }

    [Test]
    public async Task FlowGraphModule_RendersMatrixLegs()
    {
        var js = await ReadWwwrootFileAsync("flow-graph.js");
        await Assert.That(js).Contains("combinations");
        await Assert.That(js).Contains("flow-job__legs");
    }

    [Test]
    public async Task MainJs_FlowDetail_ShowsBackgroundAndMatrixCombinations()
    {
        var js = await ReadWwwrootFileAsync("main.js");
        await Assert.That(js).Contains("combinations");
        await Assert.That(js).Contains("background");
    }

    [Test]
    public async Task FlowGraphModule_SupportsSelectionHighlightAndDiagnosticMarkers()
    {
        var js = await ReadWwwrootFileAsync("flow-graph.js");
        // Clicking a job/step highlights it.
        await Assert.That(js).Contains("flow-node--selected");
        // Lint diagnostics map to the innermost step (or the job) by source line.
        await Assert.That(js).Contains("diagnostics");
        await Assert.That(js).Contains("flow-marker");
        await Assert.That(js).Contains("flow-job__diagbadge");
    }

    [Test]
    public async Task MainJs_FlowTab_PassesDiagnosticsAndShowsThemInDetail()
    {
        var js = await ReadWwwrootFileAsync("main.js");
        await Assert.That(js).Contains("lastDiagnostics");
        await Assert.That(js).Contains("info.diagnostics");
    }

    [Test]
    public async Task FlowGraphModule_ShowsJobRuntimeInfoLine()
    {
        var js = await ReadWwwrootFileAsync("flow-graph.js");
        // Second job info line (timeout / environment), visible when zoomed in.
        // Permissions are detail-panel only: they truncate too easily on the node.
        await Assert.That(js).Contains("flow-job__info");
        await Assert.That(js).Contains("timeoutMinutes");
        await Assert.That(js).Contains("environment");
        await Assert.That(js.Contains("permissions", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task MainJs_FlowDetail_ShowsRuntimeSettings()
    {
        var js = await ReadWwwrootFileAsync("main.js");
        await Assert.That(js).Contains("timeout-minutes");
        await Assert.That(js).Contains("continue-on-error");
        await Assert.That(js).Contains("'permissions'");
        await Assert.That(js).Contains("working-directory");
        await Assert.That(js).Contains("'with'");
        // Background join status comes from the flow-json contract, not a JS-side derivation.
        await Assert.That(js).Contains("backgroundOutcome");
        await Assert.That(js.Contains("bgStatus", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task IndexTemplate_HasWorkflowInfoStrip()
    {
        var html = await ReadWwwrootFileAsync("index.html");
        await Assert.That(html).Contains("id=\"flow-workflow-info\"", StringComparison.Ordinal);
    }

    [Test]
    public async Task MainJs_RendersWorkflowContextStrip()
    {
        var js = await ReadWwwrootFileAsync("main.js");
        // One chip per trigger event; schedule and concurrency chips open the detail panel.
        await Assert.That(js).Contains("flow-workflow-info");
        await Assert.That(js).Contains("schedules");
        await Assert.That(js).Contains("concurrency");
        await Assert.That(js).Contains("cancel-in-progress");
        await Assert.That(js).Contains("flow-workflow-info__chip--clickable");
        await Assert.That(js).Contains("showFlowContextDetail");
    }

    [Test]
    public async Task Stylesheet_DefinesWorkflowInfoStripClass()
    {
        var css = await ReadWwwrootFileAsync("style.css");
        await Assert.That(css).Contains(".flow-workflow-info");
        await Assert.That(css).Contains(".flow-workflow-info__chip--clickable");
    }

    [Test]
    public async Task Stylesheet_DefinesJobInfoLineHiddenAtFarZoom()
    {
        var css = await ReadWwwrootFileAsync("style.css");
        await Assert.That(css).Contains(".flow-job__info");
        await Assert.That(css).Contains(".flow-svg--lod0 .flow-job__info");
    }

    [Test]
    public async Task MainJs_FlowSelection_HighlightsEditorLines()
    {
        var js = await ReadWwwrootFileAsync("main.js");
        // Clicking a flow node highlights and scrolls to the source lines in the editor.
        await Assert.That(js).Contains("flow-hl-line");
        await Assert.That(js).Contains("addLineClass");
        await Assert.That(js).Contains("removeLineClass");
        await Assert.That(js).Contains("scrollIntoView(");
    }

    [Test]
    public async Task Stylesheet_DefinesEditorFlowHighlightClass()
    {
        var css = await ReadWwwrootFileAsync("style.css");
        await Assert.That(css).Contains(".flow-hl-line");
    }

    [Test]
    public async Task Stylesheet_DefinesSelectionAndMarkerClasses()
    {
        var css = await ReadWwwrootFileAsync("style.css");
        await Assert.That(css).Contains(".flow-node--selected");
        await Assert.That(css).Contains(".flow-marker--error");
        await Assert.That(css).Contains(".flow-marker--warning");
        await Assert.That(css).Contains(".flow-job__diagbadge");
    }

    [Test]
    public async Task Stylesheet_DefinesLodAndStepFlowClasses()
    {
        var css = await ReadWwwrootFileAsync("style.css");
        await Assert.That(css).Contains(".flow-svg--lod0");
        await Assert.That(css).Contains(".flow-step-edge");
        await Assert.That(css).Contains(".flow-job__legs");
        await Assert.That(css).Contains(".flow-job__summary");
    }

    [Test]
    public async Task FlowGraph_ReusableJobs_AreVisuallyDistinct()
    {
        var js = await ReadWwwrootFileAsync("flow-graph.js");
        // Reusable-workflow call jobs get an explicit label, not just a dashed border.
        await Assert.That(js).Contains("flow-job--reusable");
        await Assert.That(js).Contains("⧉ reusable");

        var css = await ReadWwwrootFileAsync("style.css");
        // Distinct header tint so the node reads differently from normal jobs at any LOD.
        await Assert.That(css).Contains(".flow-job--reusable .flow-job__header");
    }

    [Test]
    public async Task Stylesheet_DefinesFlowTabAndGraphClasses()
    {
        var css = await ReadWwwrootFileAsync("style.css");
        await Assert.That(css).Contains(".results-tabs");
        await Assert.That(css).Contains(".results-tab--active");
        await Assert.That(css).Contains(".flow-graph");
        await Assert.That(css).Contains(".flow-detail");
        await Assert.That(css).Contains(".flow-parallel-boundary");
        await Assert.That(css).Contains(".flow-edge");
    }

    private static async Task<string> ReadWwwrootFileAsync(string fileName)
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", fileName);
        return await File.ReadAllTextAsync(path);
    }
}
