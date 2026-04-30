using System.Text.RegularExpressions;

namespace Seiton.Playground.Tests;

/// <summary>
/// Fast structural checks against source <c>wwwroot</c> assets (no browser, no publish).
/// Fingerprint placeholders in <c>index.html</c> must stay intact so MSBuild can rewrite URLs.
/// </summary>
public sealed class PlaygroundHtmlContractTests
{
    [Test]
    public async Task IndexTemplate_PreservesFingerprintPlaceholdersForStyleAndMainScript()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains("href=\"style#[.{fingerprint}].css\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("src=\"main#[.{fingerprint}].js\"", StringComparison.Ordinal);
        await Assert.That(html.Contains("href=\"style.css\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(html.Contains("src=\"main.js\"", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task IndexTemplate_HasStableShellLandmarksForLayout()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains("id=\"linter\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"editor-wrap\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"editor\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("class=\"split-pane results-column\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"header-bar\"", StringComparison.Ordinal);
    }

    [Test]
    public async Task Stylesheet_UsesTwoColumnGridForMainLinterRegion()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "style.css");
        var css = await File.ReadAllTextAsync(path);
        await Assert.That(css).Contains("#linter");
        await Assert.That(css).Contains("display: grid");
        await Assert.That(css).Contains("grid-template-columns: 1fr 1fr");
    }

    [Test]
    public async Task Stylesheet_CentersErrorGutterMarkersVertically()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "style.css");
        var css = await File.ReadAllTextAsync(path);
        var normalized = Regex.Replace(css, @"\s+", " ");
        await Assert.That(normalized).Contains(
            "#editor-wrap .CodeMirror .CodeMirror-gutter.error-marker .CodeMirror-gutter-elt");
        await Assert.That(normalized).Contains("display: flex");
        await Assert.That(normalized).Contains("align-items: center");
    }

    private static async Task<string> ReadSourceIndexHtmlAsync()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "index.html");
        return await File.ReadAllTextAsync(path);
    }
}
