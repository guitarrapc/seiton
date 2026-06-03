using Microsoft.Playwright;
using Seiton.Playground;

namespace Seiton.Playground.Tests;

/// <summary>
/// Browser tests for v2 Share URL restore (YAML + config + file path).
/// Uses <see cref="PlaygroundUiBrowserSession"/> (same gate/teardown as layout UI tests).
/// </summary>
[NotInParallel(PlaygroundUiTestHost.ParallelLockKey)]
public sealed class PlaygroundShareRestoreUiTests
{
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

        var values = await LoadEditorsFromShareHashAsync(host, hash);

        await Assert.That(NormalizeNewlines(values.Yaml)).IsEqualTo(NormalizeNewlines(ShareYaml));
        await Assert.That(NormalizeNewlines(values.Config)).IsEqualTo(NormalizeNewlines(ShareConfig));
        await Assert.That(values.FilePath).IsEqualTo(".github/workflows/test.yml");
    }

    [Test]
    public async Task ShareUrl_LegacyYamlOnlyHash_RestoresYamlOnly()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync();
        var hash = PlaygroundSharePayload.EncodeLegacyYamlOnly(ShareYaml);

        var values = await LoadEditorsFromShareHashAsync(host, hash);

        await Assert.That(NormalizeNewlines(values.Yaml)).IsEqualTo(NormalizeNewlines(ShareYaml));
        await Assert.That(values.Config).IsEqualTo("");
    }

    private static string NormalizeNewlines(string s)
        => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static async Task<EditorValues> LoadEditorsFromShareHashAsync(PlaygroundUiTestHost.HostState host, string hash)
    {
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await PlaygroundUiLayoutTests.GotoPlaygroundAndWaitForLinterGridAsync(page, $"{host.BaseUrl}#{hash}");

        return await page.EvaluateAsync<EditorValues>(
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
    }

    private sealed class EditorValues
    {
        public string Yaml { get; set; } = "";
        public string Config { get; set; } = "";
        public string FilePath { get; set; } = "";
    }
}
