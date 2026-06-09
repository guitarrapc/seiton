using Microsoft.Playwright;

namespace Seiton.Playground.Tests;

/// <summary>
/// Browser WASM tests for Playground "memory access out of bounds" while editing workflows.
/// Uses <c>?seitonTestHooks=1</c> and Release+AOT publish to match production.
/// </summary>
[NotInParallel(PlaygroundUiTestHost.ParallelLockKey)]
public sealed class PlaygroundWasmMemoryOobUiTests
{
    private const string FullFixConfig = """
        # NOTE: enable-network uses the GitHub API (unauthenticated, 60 req/hr limit).
        # SHA/digest pinning is resolved via api.github.com when "Apply fixes" is clicked.
        fix:
          defaults:
            job-timeout-minutes: 15
          pinning:
            enable-network: true
            min-age-days: 14
          images:
            enable-network: true

        rules:
          runner-no-latest:
            fix-mapping:
              ubuntu-latest: "ubuntu-24.04"
              windows-latest: "windows-2025"
              macos-latest: "macos-15"
        """;

    /// <summary>Default sample workflow from <c>main.js</c> <c>SAMPLES.default</c> (without comment line).</summary>
    private const string DefaultWorkflowBody = """
        on:
          push:
            branch: main
            tags:
              - 'v\d+'
        jobs:
          test:
            strategy:
              matrix:
                os: [macos-latest, linux-latest]
            runs-on: ${{ matrix.os }}
            steps:
              - run: echo "Checking commit '${{ github.event.head_commit.message }}'"
              - uses: actions/checkout@v4
              - uses: actions/setup-node@v4
                with:
                  node_version: 18.x
              - uses: actions/cache@v4
                with:
                  path: ~/.npm
                  key: ${{ matrix.platform }}-node-${{ hashFiles('**/package-lock.json') }}
                if: ${{ github.repository.permissions.admin == true }}
              - run: npm install && npm test
        """;

    /// <summary>Must align with step list indent in <see cref="DefaultWorkflowBody"/> (6 spaces before <c>-</c>).</summary>
    private const string TrailingStepSuffix = """

              - uses: guitarrapc/setup-seiton@v1.0.0
                with:
                  version: 
        """;

    /// <summary>
    /// Stable suffixes while typing the trailing step (user ends at <c>version: </c>).
    /// Intermediate fragments like bare <c>- uses:</c> can still abort the Mono WASM runtime; not modeled here.
    /// </summary>
    private static readonly string[] KeystrokeSuffixes =
    [
        "      - uses: guitarrapc/setup-seiton@v1.0.0",
        "      - uses: guitarrapc/setup-seiton@v1.0.0\n        with:\n          version: ",
        TrailingStepSuffix.TrimStart('\n'),
    ];

    [Test]
    public async Task WasmLint_KeystrokeSuffixes_DoNotThrowMemoryOob()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();

        var failures = new List<string>();
        for (var i = 0; i < KeystrokeSuffixes.Length; i++)
        {
            // Fresh page per suffix so a WASM trap in one variant does not kill the runtime for later cases.
            await using var suffixContext = await browser.NewContextAsync();
            var suffixPage = await OpenPlaygroundWithTestHooksAsync(suffixContext, host.BaseUrl);
            try
            {
                await ApplyFullFixConfigViaHooksAsync(suffixPage);

                var result = await RunLintAppendingSuffixAsync(suffixPage, KeystrokeSuffixes[i]);
                if (!result.Ok)
                {
                    failures.Add($"[{i}] suffix len={KeystrokeSuffixes[i].Length}: {result.Error}");
                    continue;
                }

                if (result.InternalError)
                {
                    failures.Add($"[{i}] internal-error diagnostic");
                }
            }
            finally
            {
                await suffixPage.CloseAsync();
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because($"WASM RunLint failures:\n{string.Join('\n', failures)}");
    }

    [Test]
    public async Task WasmLint_FinalIncompleteTrailingStep_DoNotThrowMemoryOob()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenPlaygroundWithTestHooksAsync(context, host.BaseUrl);

        await ApplyFullFixConfigViaHooksAsync(page);

        var result = await RunLintAppendingSuffixAsync(page, TrailingStepSuffix);

        await Assert.That(result.Ok).IsTrue().Because(result.Error ?? "unknown");
        await Assert.That(result.Error ?? "").DoesNotContain("memory access out of bounds");
        await Assert.That(result.InternalError).IsFalse();
    }

    [Test]
    public async Task WasmLint_BareUsesLine_IsDeferred_AndCompletedUsesIsLinted()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenPlaygroundWithTestHooksAsync(context, host.BaseUrl);

        await ApplyFullFixConfigViaHooksAsync(page);
        var baseYaml = await GetEditorWorkflowBaseAsync(page);

        var deferred = await RunLintViaHooksAsync(page, baseYaml + "      - uses:");
        await Assert.That(deferred.Ok).IsTrue().Because(deferred.Error ?? "unknown");
        await Assert.That(deferred.Deferred).IsTrue();
        await Assert.That(deferred.Diagnostics ?? []).Count().IsEqualTo(0);

        var linted = await RunLintViaHooksAsync(page, baseYaml + "      - uses: actions/checkout@v4");
        await Assert.That(linted.Ok).IsTrue().Because(linted.Error ?? "unknown");
        await Assert.That(linted.Deferred).IsFalse();
    }

    [Test]
    public async Task WasmLint_AlternatingBufferSizes_DoNotThrowMemoryOob()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenPlaygroundWithTestHooksAsync(context, host.BaseUrl);

        await ApplyFullFixConfigViaHooksAsync(page);

        var baseYaml = await GetEditorWorkflowBaseAsync(page);

        for (var round = 0; round < 8; round++)
        {
            var yaml = round % 2 == 0 ? baseYaml : baseYaml + TrailingStepSuffix;
            var result = await RunLintViaHooksAsync(page, yaml);
            await Assert.That(result.Ok).IsTrue()
                .Because($"round {round}: {result.Error ?? "ok"}");
            await Assert.That(result.Error ?? "").DoesNotContain("memory access out of bounds");
        }
    }

    [Test]
    public async Task TypingTrailingStep_DoesNotShowMemoryOobToast()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1100, Height = 800 },
        });
        var page = await OpenPlaygroundWithTestHooksAsync(context, host.BaseUrl);

        await page.SelectOptionAsync("#config-template-select", "fullFix");
        await page.WaitForTimeoutAsync(700);

        var trailingSuffix = "\n" + TrailingStepSuffix.TrimEnd();
        await page.EvaluateAsync(
            """
            ([suffix]) => {
              const cm = document.querySelector('#editor .CodeMirror')?.CodeMirror;
              if (!cm) throw new Error('workflow editor missing');
              cm.setValue(cm.getValue() + suffix);
              cm.refresh();
            }
            """,
            trailingSuffix);

        await page.WaitForTimeoutAsync(450);

        var oobToast = page.Locator("#toast-stack .toast--error", new PageLocatorOptions
        {
            HasText = "memory access out of bounds",
        });
        await Assert.That(await oobToast.CountAsync()).IsEqualTo(0);
        await Assert.That(await page.EvaluateAsync<bool>(
            "() => globalThis.__SEITON_PLAYGROUND_TEST__?.getRuntimeAlive?.() !== false")).IsTrue();
    }

    private static async Task<IPage> OpenPlaygroundWithTestHooksAsync(IBrowserContext context, string baseUrl)
    {
        var page = await context.NewPageAsync();
        var oobMessages = new List<string>();
        page.Console += (_, msg) =>
        {
            if (msg.Text.Contains("memory access out of bounds", StringComparison.OrdinalIgnoreCase))
            {
                oobMessages.Add(msg.Text);
            }
        };

        await PlaygroundUiLayoutTests.GotoPlaygroundAndWaitForLinterGridAsync(
            page,
            $"{baseUrl.TrimEnd('/')}/?seitonTestHooks=1");

        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.runLint === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 120_000 });

        return page;
    }

    private static async Task ApplyFullFixConfigViaHooksAsync(IPage page)
    {
        var setResult = await page.EvaluateAsync<SetConfigHookResult>(
            """
            (cfg) => globalThis.__SEITON_PLAYGROUND_TEST__.setConfig(cfg)
            """,
            FullFixConfig);

        await Assert.That(setResult.Diagnostics).IsEmpty();
        await page.WaitForTimeoutAsync(100);
    }

    /// <summary>Workflow text currently in the editor (default sample after load + setConfig).</summary>
    private static async Task<string> GetEditorWorkflowBaseAsync(IPage page)
    {
        return await page.EvaluateAsync<string>(
            """
            () => {
              const cm = document.querySelector('#editor .CodeMirror')?.CodeMirror;
              if (!cm) throw new Error('workflow editor missing');
              return cm.getValue();
            }
            """);
    }

    private static async Task<LintHookResult> RunLintViaHooksAsync(IPage page, string yaml)
    {
        return await page.EvaluateAsync<LintHookResult>(
            """
            (src) => globalThis.__SEITON_PLAYGROUND_TEST__.runLint(src, '.github/workflows/ci.yml')
            """,
            yaml);
    }

    private static async Task<LintHookResult> RunLintAppendingSuffixAsync(IPage page, string suffix)
    {
        return await page.EvaluateAsync<LintHookResult>(
            """
            (suffix) => {
              const cm = document.querySelector('#editor .CodeMirror')?.CodeMirror;
              if (!cm) throw new Error('workflow editor missing');
              return globalThis.__SEITON_PLAYGROUND_TEST__.runLint(cm.getValue() + suffix, '.github/workflows/ci.yml');
            }
            """,
            suffix);
    }

    private sealed class LintHookResult
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public bool InternalError { get; set; }
        public bool Deferred { get; set; }
        public object[]? Diagnostics { get; set; }
    }

    private sealed class SetConfigHookResult
    {
        public object[] Diagnostics { get; set; } = [];
    }
}
