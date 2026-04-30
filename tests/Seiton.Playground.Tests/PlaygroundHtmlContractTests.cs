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
    public async Task IndexTemplate_UrlFetchInput_HasAccessibleNameAndPlaceholder()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains("id=\"url-input\" aria-label=\"YAML URL\"", StringComparison.Ordinal);
        await Assert.That(html.Contains("https:// raw ", StringComparison.Ordinal)).IsFalse();
        await Assert.That(html).Contains(
            "placeholder=\"https://raw GitHub/Gist YAML URL\"",
            StringComparison.Ordinal);
    }

    [Test]
    public async Task IndexTemplate_CodeMirrorCdnAssetsHaveSubresourceIntegrity()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains("codemirror/5.65.16/", StringComparison.Ordinal);
        var matches = Regex.Matches(html, @"integrity=""sha384-[A-Za-z0-9+/=]+""");
        await Assert.That(matches.Count).IsEqualTo(5);

        // SHA-384 of files from cdnjs 5.65.16 — recompute when bumping the CodeMirror version in index.html.
        foreach (var digest in Codemirror_5_65_16_Sha384Digests)
        {
            await Assert.That(html).Contains("sha384-" + digest, StringComparison.Ordinal);
        }
    }

    private static readonly string[] Codemirror_5_65_16_Sha384Digests =
    [
        "zaeBlB/vwYsDRSlFajnDd7OydJ0cWk+c2OWybl3eSUf6hW2EbhlCsQPqKr3gkznT",
        "eZTPTN0EvJdn23s24UDYJmUM2T7C2ZFa3qFLypeBruJv8mZeTusKUAO/j5zPAQ6l",
        "ZYmwuq4n2gOcNxMSiJ6jyTj+BbIrilr7p6dlq6q5nmSWKmsH9UU4K1qqjycMkfmR",
        "9q49Jm3hZMwxEMLImsxPxLiaptHpFz1PVa26Dg6SVIO+rj5kx0cgOM2+4ikKJFH9",
        "hcxaXyAtJ30s2NeDu1OHWsQRiHiWuYLTbI596+YFb+f2pFhzO0mDuahZziRPPDxg",
    ];

    [Test]
    public async Task IndexTemplate_ImportMapScript_ContainsValidMinimalJson()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains("<script type=\"importmap\">{}</script>", StringComparison.Ordinal);
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
    public async Task IndexTemplate_FooterThemeCycleButton_HasIconSvgsAndAccessibleName()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains("footer-copy-row", StringComparison.Ordinal);
        await Assert.That(html).Contains("footer-copy-text", StringComparison.Ordinal);
        await Assert.That(html).Contains("footer-copy-sep", StringComparison.Ordinal);
        await Assert.That(html).Contains("> | </span>", StringComparison.Ordinal);
        await Assert.That(html.Contains("footer-theme-wrap", StringComparison.Ordinal)).IsFalse();
        var ixFooter = html.IndexOf("<footer>", StringComparison.Ordinal);
        await Assert.That(ixFooter >= 0).IsTrue();
        var beforeFooter = html[..ixFooter];
        await Assert.That(beforeFooter.Contains("repo-mark", StringComparison.Ordinal)).IsFalse();
        await Assert.That(Regex.IsMatch(html,
                @"<footer>[\s\S]*?footer-copy-row[\s\S]*?repo-mark[\s\S]*?footer-copy-text",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(3)))
            .IsTrue();
        await Assert.That(html).Contains("aria-label=\"seiton on GitHub\"", StringComparison.Ordinal);
        await Assert.That(Regex.IsMatch(html, @"id\s*=\s*""theme-cycle-btn""[\s\S]*?<svg\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))).IsTrue();
        await Assert.That(html).Contains("data-theme-mode=\"system\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("theme-cycle-btn__svg--system", StringComparison.Ordinal);
        await Assert.That(html).Contains("theme-cycle-btn__svg--light", StringComparison.Ordinal);
        await Assert.That(html).Contains("theme-cycle-btn__svg--dark", StringComparison.Ordinal);
        await Assert.That(html.Contains("Color: System", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Stylesheet_FooterCopyRowUsesFlexForCopyrightAndTheme()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "style.css");
        var css = await File.ReadAllTextAsync(path);
        await Assert.That(css).Contains(".footer-copy-row");
        await Assert.That(css).Contains(".footer-copy-row .repo-mark");
        await Assert.That(css).Contains(".footer-copy-sep");
        await Assert.That(css.Contains(".footer-theme-wrap", StringComparison.Ordinal)).IsFalse();
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
