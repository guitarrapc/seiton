using Microsoft.Playwright;
using Seiton.Playground;

namespace Seiton.Playground.Tests;

/// <summary>
/// Browser tests for v2 Share URL restore (YAML + config + file path).
/// </summary>
[NotInParallel(PlaygroundUiTestHost.ParallelLockKey)]
public sealed class PlaygroundShareRestoreUiTests
{
    private static readonly SemaphoreSlim s_browserGate = new(1, 1);
    private static IPlaywright? s_playwright;
    private static IBrowser? s_browser;

    private const string ShareYaml = """
        on: push
        jobs:
          ci:
            runs-on: ubuntu-latest
        """;

    private const string ShareConfig = """
        rules:
          job-timeout-minutes-required:
            enabled: false
        """;

    [Test]
    public async Task ShareUrl_V2Hash_RestoresYamlAndConfigInEditors()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var hash = PlaygroundSharePayload.Encode(
            new PlaygroundSharePayload.State(ShareYaml, ShareConfig, ".github/workflows/test.yml"));

        var browser = await GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{host.BaseUrl}#{hash}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 120_000,
        });

        await page.WaitForSelectorAsync("#loading", new() { State = WaitForSelectorState.Hidden, Timeout = 120_000 });

        var values = await page.EvaluateAsync<EditorValues>(
            """
            () => {
              const yamlEl = document.querySelector('#editor .CodeMirror');
              const cfgEl = document.querySelector('#config-editor .CodeMirror');
              return {
                yaml: yamlEl?.CodeMirror?.getValue?.() ?? '',
                config: cfgEl?.CodeMirror?.getValue?.() ?? '',
                filePath: document.getElementById('filetype-select')?.value ?? '',
              };
            }
            """);

        await Assert.That(NormalizeNewlines(values.Yaml)).IsEqualTo(NormalizeNewlines(ShareYaml));
        await Assert.That(NormalizeNewlines(values.Config)).IsEqualTo(NormalizeNewlines(ShareConfig));
        await Assert.That(values.FilePath).IsEqualTo(".github/workflows/test.yml");
    }

    [Test]
    public async Task ShareUrl_LegacyYamlOnlyHash_RestoresYamlOnly()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var hash = PlaygroundSharePayload.EncodeLegacyYamlOnly(ShareYaml);

        var browser = await GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{host.BaseUrl}#{hash}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 120_000,
        });

        await page.WaitForSelectorAsync("#loading", new() { State = WaitForSelectorState.Hidden, Timeout = 120_000 });

        var values = await page.EvaluateAsync<EditorValues>(
            """
            () => {
              const yamlEl = document.querySelector('#editor .CodeMirror');
              const cfgEl = document.querySelector('#config-editor .CodeMirror');
              return {
                yaml: yamlEl?.CodeMirror?.getValue?.() ?? '',
                config: cfgEl?.CodeMirror?.getValue?.() ?? '',
                filePath: document.getElementById('filetype-select')?.value ?? '',
              };
            }
            """);

        await Assert.That(NormalizeNewlines(values.Yaml)).IsEqualTo(NormalizeNewlines(ShareYaml));
        await Assert.That(values.Config).IsEqualTo("");
    }

    private static string NormalizeNewlines(string s)
        => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static async Task<IBrowser> GetBrowserAsync()
    {
        await s_browserGate.WaitAsync();
        try
        {
            if (s_browser is { } existing && existing.IsConnected)
            {
                return existing;
            }

            s_playwright ??= await Playwright.CreateAsync();
            s_browser = await s_playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            return s_browser;
        }
        finally
        {
            s_browserGate.Release();
        }
    }

    private sealed class EditorValues
    {
        public string Yaml { get; set; } = "";
        public string Config { get; set; } = "";
        public string FilePath { get; set; } = "";
    }
}
