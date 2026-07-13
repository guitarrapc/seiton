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
