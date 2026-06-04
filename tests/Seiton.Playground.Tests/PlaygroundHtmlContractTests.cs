using System.Text.RegularExpressions;

namespace Seiton.Playground.Tests;

/// <summary>
/// Fast structural checks against source <c>wwwroot</c> assets (no browser, no publish).
/// Fingerprint placeholders in <c>index.html</c> must stay intact so MSBuild can rewrite URLs.
/// </summary>
[NotInParallel(PlaygroundTestParallelism.AssemblyLockKey)]
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
    public async Task IndexTemplate_AboutPlaygroundSection_AppearsBeforeReferences()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains("id=\"about-playground-heading\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("class=\"playground-about\"", StringComparison.Ordinal);
        var ixAbout = html.IndexOf("class=\"playground-about\"", StringComparison.Ordinal);
        var ixResources = html.IndexOf("class=\"resources\"", StringComparison.Ordinal);
        await Assert.That(ixAbout >= 0 && ixResources >= 0 && ixAbout < ixResources).IsTrue();
    }

    [Test]
    public async Task IndexTemplate_FooterThemeCycleButton_HasIconSvgsAndAccessibleName()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains("footer-copy-row", StringComparison.Ordinal);
        await Assert.That(html).Contains("footer-copy-text", StringComparison.Ordinal);
        await Assert.That(html).Contains("footer-copy-sep", StringComparison.Ordinal);
        await Assert.That(Regex.Matches(html, @"class\s*=\s*""footer-copy-sep""", RegexOptions.IgnoreCase).Count).IsEqualTo(2);
        await Assert.That(html).Contains("|</span>", StringComparison.Ordinal);
        await Assert.That(html.Contains("footer-theme-wrap", StringComparison.Ordinal)).IsFalse();
        var ixFooter = html.IndexOf("<footer>", StringComparison.Ordinal);
        await Assert.That(ixFooter >= 0).IsTrue();
        var beforeFooter = html[..ixFooter];
        await Assert.That(beforeFooter.Contains("repo-mark", StringComparison.Ordinal)).IsFalse();
        await Assert.That(Regex.IsMatch(html,
                @"<footer>[\s\S]*?footer-copy-row[\s\S]*?repo-mark[\s\S]*?footer-copy-text",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(3)))
            .IsTrue();
        await Assert.That(html).Contains("seiton on GitHub", StringComparison.Ordinal);
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
        await Assert.That(css).Contains("column-gap");
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

    [Test]
    public async Task Stylesheet_DefinesSeverityChipClassesForAllLevels()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "style.css");
        var css = await File.ReadAllTextAsync(path);
        await Assert.That(css).Contains(".severity-chip");
        await Assert.That(css).Contains(".severity-chip--error");
        await Assert.That(css).Contains(".severity-chip--warning");
        await Assert.That(css).Contains(".severity-chip--info");
    }

    [Test]
    public async Task Stylesheet_DefinesInfoCssVariable()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "style.css");
        var css = await File.ReadAllTextAsync(path);
        await Assert.That(css).Contains("--info:");
    }

    [Test]
    public async Task MainJs_RenderResults_CreatesSeverityChipElements()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "main.js");
        var js = await File.ReadAllTextAsync(path);
        await Assert.That(js).Contains("severity-chip");
        await Assert.That(js).Contains("diag.severity");
    }

    [Test]
    public async Task MainJs_RenderResults_SupportsCollapsibleDiagnosticMessages()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "main.js");
        var js = await File.ReadAllTextAsync(path);
        await Assert.That(js).Contains("appendDiagnosticDescriptionCell");
        await Assert.That(js).Contains("shouldCollapseDiagMessage");
        await Assert.That(js).Contains("maybeAttachDiagMessageToggle");
        await Assert.That(js).Contains("countRenderedDiagMessageLines");
        await Assert.That(js).Contains("diag-message--collapsed");
        await Assert.That(js).Contains("diag-message-toggle");
        await Assert.That(js).Contains("DIAG_MESSAGE_COLLAPSE_MIN_CHARS");
    }

    [Test]
    public async Task Stylesheet_DefinesCollapsibleDiagnosticMessageClasses()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "style.css");
        var css = await File.ReadAllTextAsync(path);
        await Assert.That(css).Contains(".diag-message--collapsed");
        await Assert.That(css).Contains(".diag-message-toggle");
        await Assert.That(css).Contains("-webkit-line-clamp: 3");
    }

    [Test]
    public async Task Stylesheet_DefinesGutterMarkerInfoClass()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "style.css");
        var css = await File.ReadAllTextAsync(path);
        await Assert.That(css).Contains(".gutter-marker--info");
    }

    [Test]
    public async Task MainJs_GutterMarker_DistinguishesInfoFromWarning()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "main.js");
        var js = await File.ReadAllTextAsync(path);
        await Assert.That(js).Contains("gutter-marker--info");
    }

    [Test]
    public async Task Stylesheet_DefinesRowSeverityBorderStyles()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "style.css");
        var css = await File.ReadAllTextAsync(path);
        await Assert.That(css).Contains("[data-severity=");
        await Assert.That(css).Contains("error");
        await Assert.That(css).Contains("warning");
        await Assert.That(css).Contains("info");
        await Assert.That(css).Contains("border-left:");
    }

    [Test]
    public async Task MainJs_RenderResults_SetsDataSeverityOnRows()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "main.js");
        var js = await File.ReadAllTextAsync(path);
        await Assert.That(js).Contains("dataset.severity");
    }

    [Test]
    public async Task IndexTemplate_HasConfigPanelLandmarks()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains("id=\"config-panel\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"config-editor\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"config-toggle-btn\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"config-editor-wrap\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("id=\"config-diagnostics\"", StringComparison.Ordinal);
    }

    [Test]
    public async Task IndexTemplate_ConfigToggle_HasAriaExpandedAndControls()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains("aria-expanded=\"true\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("aria-controls=\"config-editor-wrap\"", StringComparison.Ordinal);
    }

    [Test]
    public async Task Stylesheet_DefinesConfigPanelClasses()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "style.css");
        var css = await File.ReadAllTextAsync(path);
        await Assert.That(css).Contains(".config-panel");
        await Assert.That(css).Contains(".config-panel--collapsed");
        await Assert.That(css).Contains(".config-panel__toggle");
        await Assert.That(css).Contains(".config-panel__body");
        await Assert.That(css).Contains(".config-diagnostics");
    }

    [Test]
    public async Task MainJs_ConfigEditor_HasDebounceAndSetConfigCall()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "main.js");
        var js = await File.ReadAllTextAsync(path);
        await Assert.That(js).Contains("CONFIG_DEBOUNCE_MS");
        await Assert.That(js).Contains("setConfig(");
        await Assert.That(js).Contains("configEditor");
        await Assert.That(js).Contains("renderConfigDiagnostics");
    }

    [Test]
    public async Task IndexTemplate_HasConfigTemplateSelect()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains("id=\"config-template-select\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("value=\"timeoutAndLatest\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("value=\"fullFix\"", StringComparison.Ordinal);
        await Assert.That(html).Contains("value=\"exclusions\"", StringComparison.Ordinal);
    }

    [Test]
    public async Task IndexTemplate_PermalinkButton_StatesYamlAndConfigInShareUrl()
    {
        var html = await ReadSourceIndexHtmlAsync();
        const string expected =
            "Share — copy link with workflow YAML and config in URL hash";
        await Assert.That(html).Contains($"id=\"permalink-btn\"", StringComparison.Ordinal);
        await Assert.That(html).Contains($"title=\"{expected}\"", StringComparison.Ordinal);
        await Assert.That(html).Contains($"aria-label=\"{expected}\"", StringComparison.Ordinal);
    }

    [Test]
    public async Task IndexTemplate_AboutPlayground_StatesShareIncludesConfigWithFallback()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains("workflow YAML and lint config", StringComparison.Ordinal);
        await Assert.That(html).Contains("workflow YAML only", StringComparison.Ordinal);
        await Assert.That(html).Contains("clipboard", StringComparison.Ordinal);
    }

    [Test]
    public async Task IndexTemplate_ConfigPanel_StatesConfigIncludedInShareWhenUrlFits()
    {
        var html = await ReadSourceIndexHtmlAsync();
        await Assert.That(html).Contains(
            "title=\"Toggle lint configuration editor. Included in Share links when the URL is not too long.\"",
            StringComparison.Ordinal);
    }

    [Test]
    public async Task MainJs_ImportsSharePayloadModule()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "main.js");
        var js = await File.ReadAllTextAsync(path);
        await Assert.That(js).Contains("from './share-payload.js'");
        await Assert.That(js).Contains("encodeShareState");
        await Assert.That(js).Contains("decodeShareHash");
        await Assert.That(js).Contains("formatClipboardBundle");
    }

    [Test]
    public async Task SharePayloadModule_DefinesV2CodecAndLimits()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "share-payload.js");
        var js = await File.ReadAllTextAsync(path);
        await Assert.That(js).Contains("SHARE_PAYLOAD_VERSION = 2");
        await Assert.That(js).Contains("MAX_SHARE_HASH_LENGTH");
        await Assert.That(js).Contains("encodeShareState");
        await Assert.That(js).Contains("encodeYamlOnlyShare");
    }

    [Test]
    public async Task MainJs_ConfigTemplates_HasAllTemplateKeys()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "main.js");
        var js = await File.ReadAllTextAsync(path);
        await Assert.That(js).Contains("CONFIG_TEMPLATES");
        await Assert.That(js).Contains("timeoutAndLatest:");
        await Assert.That(js).Contains("fullFix:");
        await Assert.That(js).Contains("exclusions:");
        await Assert.That(js).Contains("job-timeout-minutes: 15");
        await Assert.That(js).Contains("enable-network: true");
    }

    [Test]
    public async Task Stylesheet_DefinesConfigTemplateSelectClass()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "style.css");
        var css = await File.ReadAllTextAsync(path);
        await Assert.That(css).Contains(".config-panel__template-select");
        await Assert.That(css).Contains(".config-panel__header");
    }

    private static async Task<string> ReadSourceIndexHtmlAsync()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "Seiton.Playground", "wwwroot", "index.html");
        return await File.ReadAllTextAsync(path);
    }
}
