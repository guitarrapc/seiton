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

    private const string SimpleWorkflowWithJobKeyPlaceholder = """
        # Paste your workflow YAML to this code editor

        on:
          push:
            branch: main

        jobs:
          test:
            __JOB_KEY__
            runs-on: ubuntu-latest
            steps:
        - uses: actions/checkout@v6
        - uses: actions/cache@v4
          with:
          path: ~/.npm
          key: ubuntu-node-${{ hashFiles('**/package-lock.json') }}
        - run: npm install && npm test
        """;

    private const string ValidSimpleWorkflowWithJobKeyPlaceholder = """
        # Paste your workflow YAML to this code editor

        on:
          push:
            branch: main

        jobs:
          test:
            __JOB_KEY__
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v6
              - uses: actions/cache@v4
                with:
                  path: ~/.npm
                  key: ubuntu-node-${{ hashFiles('**/package-lock.json') }}
              - run: npm install && npm test
        """;

    private const string SimpleWorkflowWithCodeMirrorIndentPlaceholder = """
        # Paste your workflow YAML to this code editor

        on:
          push:
            branch: main

        jobs:
          test:
          __JOB_KEY__
            runs-on: ubuntu-latest
            steps:
              - run: npm test
        """;

    private static readonly string[] IncompleteJobKeyPrefixes =
        ["s", "st", "str", "stra", "strat", "strate", "strateg", "strategy"];

    private static readonly string[] IncompleteJobKeys = [.. IncompleteJobKeyPrefixes, "strategy:"];

    private const string WorkflowWithStrategyLikeBlockScalar = """
        on: push
        jobs:
          test:
            runs-on: ubuntu-24.04
            steps:
              - run: |
                  strategy
                  runs-on:
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
    public async Task WasmLint_BareUsesLine_AndCompletedUses_AreLinted()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenPlaygroundWithTestHooksAsync(context, host.BaseUrl);

        await ApplyFullFixConfigViaHooksAsync(page);
        var baseYaml = await GetEditorWorkflowBaseAsync(page);

        var incomplete = await RunLintViaHooksAsync(page, baseYaml + "      - uses:");
        await Assert.That(incomplete.Ok).IsTrue().Because(incomplete.Error ?? "unknown");
        await Assert.That(incomplete.Deferred).IsFalse();

        var linted = await RunLintViaHooksAsync(page, baseYaml + "      - uses: actions/checkout@v4");
        await Assert.That(linted.Ok).IsTrue().Because(linted.Error ?? "unknown");
        await Assert.That(linted.Deferred).IsFalse();
    }

    [Test]
    public async Task WasmLint_RawCodeMirrorIntermediateYaml_Completes()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenPlaygroundWithTestHooksAsync(context, host.BaseUrl);
        var yaml = SimpleWorkflowWithCodeMirrorIndentPlaceholder.Replace(
            "__JOB_KEY__",
            "s",
            StringComparison.Ordinal);
        var lint = RunLintViaHooksAsync(page, yaml);
        await CompleteWithinAsync(lint, "linting the raw CodeMirror intermediate YAML");

        var result = await lint;
        await Assert.That(result.Ok).IsTrue().Because(result.Error ?? "unknown");
    }

    [Test]
    public async Task WasmLint_ActionMetadataPath_StillSelectsActionParser()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenPlaygroundWithTestHooksAsync(context, host.BaseUrl);
        var result = await page.EvaluateAsync<LintHookResult>(
            """
            (src) => globalThis.__SEITON_PLAYGROUND_TEST__.runLint(src, 'action.yml')
            """,
            """
            name: Composite action
            description: Test action
            runs:
              using: composite
              steps:
                - run: echo ok
                  shell: bash
            """);

        await Assert.That(result.Ok).IsTrue().Because(result.Error ?? "unknown");
        await Assert.That(result.Diagnostics ?? []).IsEmpty();
    }

    [Test]
    public async Task WasmLint_IncompleteJobKeysBeforeFollowingProperties_DoNotHang()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenPlaygroundWithTestHooksAsync(context, host.BaseUrl);

        var failures = new List<string>();
        for (var i = 0; i < IncompleteJobKeys.Length; i++)
        {
            var yaml = SimpleWorkflowWithJobKeyPlaceholder.Replace(
                "__JOB_KEY__",
                IncompleteJobKeys[i],
                StringComparison.Ordinal);
            var result = await RunLintWithinAsync(page, yaml, $"linting '{IncompleteJobKeys[i]}' with job-property indentation");
            if (!result.Ok)
            {
                failures.Add($"[{i}] '{IncompleteJobKeys[i]}': {result.Error}");
            }
            else
            {
                if (result.Deferred)
                {
                    failures.Add($"[{i}] '{IncompleteJobKeys[i]}': lint was deferred");
                }
            }
        }

        for (var i = 0; i < IncompleteJobKeyPrefixes.Length; i++)
        {
            var yaml = SimpleWorkflowWithCodeMirrorIndentPlaceholder.Replace(
                "__JOB_KEY__",
                IncompleteJobKeyPrefixes[i],
                StringComparison.Ordinal);
            var result = await RunLintWithinAsync(page, yaml, $"linting '{IncompleteJobKeyPrefixes[i]}' with CodeMirror indentation");
            if (!result.Ok || result.Deferred)
            {
                failures.Add($"CodeMirror indent [{i}] '{IncompleteJobKeyPrefixes[i]}': ok={result.Ok}, deferred={result.Deferred}, error={result.Error}");
            }
        }

        var codeMirrorCrlfYaml = SimpleWorkflowWithCodeMirrorIndentPlaceholder
            .Replace("__JOB_KEY__", "str", StringComparison.Ordinal)
            .ReplaceLineEndings("\r\n");
        var codeMirrorCrlfResult = await RunLintWithinAsync(page, codeMirrorCrlfYaml, "linting the CRLF CodeMirror strategy prefix");
        if (!codeMirrorCrlfResult.Ok || codeMirrorCrlfResult.Deferred)
        {
            failures.Add($"CRLF CodeMirror strategy prefix: ok={codeMirrorCrlfResult.Ok}, deferred={codeMirrorCrlfResult.Deferred}, error={codeMirrorCrlfResult.Error}");
        }

        var malformedCompletedYaml = SimpleWorkflowWithJobKeyPlaceholder.Replace(
            "__JOB_KEY__",
            "strategy:\n      fail-fast: false",
            StringComparison.Ordinal);
        var malformedCompletedResult = await RunLintWithinAsync(page, malformedCompletedYaml, "linting the malformed completed strategy");
        if (!malformedCompletedResult.Ok || malformedCompletedResult.Deferred)
        {
            failures.Add($"malformed completed strategy: ok={malformedCompletedResult.Ok}, deferred={malformedCompletedResult.Deferred}, error={malformedCompletedResult.Error}");
        }

        var crlfYaml = SimpleWorkflowWithJobKeyPlaceholder
            .Replace("__JOB_KEY__", "str", StringComparison.Ordinal)
            .ReplaceLineEndings("\r\n");
        var crlfResult = await RunLintWithinAsync(page, crlfYaml, "linting the CRLF strategy prefix");
        if (!crlfResult.Ok || crlfResult.Deferred)
        {
            failures.Add($"CRLF strategy prefix: ok={crlfResult.Ok}, deferred={crlfResult.Deferred}, error={crlfResult.Error}");
        }

        var completedYaml = ValidSimpleWorkflowWithJobKeyPlaceholder.Replace(
            "__JOB_KEY__",
            "strategy:\n      fail-fast: false",
            StringComparison.Ordinal);
        var completedResult = await RunLintWithinAsync(page, completedYaml, "linting the completed strategy");
        if (!completedResult.Ok || completedResult.Deferred)
        {
            failures.Add($"completed strategy: ok={completedResult.Ok}, deferred={completedResult.Deferred}, error={completedResult.Error}");
        }

        await Assert.That(failures).IsEmpty()
            .Because($"WASM RunLint failures:\n{string.Join('\n', failures)}");
    }

    [Test]
    public async Task WasmLint_StableStrategyLikeInputs_AreNotDeferred()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenPlaygroundWithTestHooksAsync(context, host.BaseUrl);

        var unknownKeyYaml = ValidSimpleWorkflowWithJobKeyPlaceholder.Replace(
            "__JOB_KEY__",
            "s:",
            StringComparison.Ordinal);
        var unknownKeyResult = await RunLintViaHooksAsync(page, unknownKeyYaml);
        await Assert.That(unknownKeyResult.Ok).IsTrue().Because(unknownKeyResult.Error ?? "unknown");
        await Assert.That(unknownKeyResult.Deferred).IsFalse();

        var blockScalarResult = await RunLintViaHooksAsync(page, WorkflowWithStrategyLikeBlockScalar);
        await Assert.That(blockScalarResult.Ok).IsTrue().Because(blockScalarResult.Error ?? "unknown");
        await Assert.That(blockScalarResult.Deferred).IsFalse();

        var stableUnknownKeyYaml = ValidSimpleWorkflowWithJobKeyPlaceholder.Replace(
            "__JOB_KEY__",
            "strategyx:",
            StringComparison.Ordinal);
        var stableUnknownKeyResult = await RunLintViaHooksAsync(page, stableUnknownKeyYaml);
        await Assert.That(stableUnknownKeyResult.Ok).IsTrue().Because(stableUnknownKeyResult.Error ?? "unknown");
        await Assert.That(stableUnknownKeyResult.Deferred).IsFalse();

        var completedSiblingJobYaml = SimpleWorkflowWithCodeMirrorIndentPlaceholder.Replace(
            "__JOB_KEY__",
            "strategy:",
            StringComparison.Ordinal);
        var completedSiblingJobResult = await RunLintViaHooksAsync(page, completedSiblingJobYaml);
        await Assert.That(completedSiblingJobResult.Ok).IsTrue().Because(completedSiblingJobResult.Error ?? "unknown");
        await Assert.That(completedSiblingJobResult.Deferred).IsFalse();

        var shortSiblingJobYaml = SimpleWorkflowWithCodeMirrorIndentPlaceholder.Replace(
            "__JOB_KEY__",
            "s:",
            StringComparison.Ordinal);
        var shortSiblingJobResult = await RunLintViaHooksAsync(page, shortSiblingJobYaml);
        await Assert.That(shortSiblingJobResult.Ok).IsTrue().Because(shortSiblingJobResult.Error ?? "unknown");
        await Assert.That(shortSiblingJobResult.Deferred).IsFalse();
    }

    [Test]
    public async Task SelectingSimpleWorkflow_ThenTypingStrategy_KeepsPageResponsive()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenPlaygroundWithTestHooksAsync(context, host.BaseUrl);

        await CompleteWithinAsync(
            page.SelectOptionAsync("#sample-select", "simple"),
            "selecting the simple workflow");
        await Task.Delay(700);
        await AssertPageResponsiveAsync(page, "linting the simple workflow");

        await page.EvaluateAsync(
            """
            () => {
              const cm = document.querySelector('#editor .CodeMirror')?.CodeMirror;
              if (!cm) throw new Error('workflow editor missing');
              cm.setCursor({ line: 7, ch: cm.getLine(7).length });
              cm.execCommand('newlineAndIndent');
              cm.focus();
            }
            """);

        foreach (var character in "strategy:")
        {
            await CompleteWithinAsync(
                page.Keyboard.TypeAsync(character.ToString()),
                $"typing '{character}'");
            await Task.Delay(700);
            await AssertPageResponsiveAsync(page, $"linting after '{character}'");
        }
    }

    [Test]
    public async Task TypingStrategyInSimpleWorkflow_DoesNotFreezeEditor()
    {
        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenPlaygroundWithTestHooksAsync(context, host.BaseUrl);

        var baseYaml = ValidSimpleWorkflowWithJobKeyPlaceholder.Replace(
            "__JOB_KEY__",
            string.Empty,
            StringComparison.Ordinal);
        await SetEditorValueAsync(page, baseYaml);
        await page.WaitForTimeoutAsync(700);

        var previousLength = 0;
        for (var i = 0; i < IncompleteJobKeys.Length; i++)
        {
            await ReplaceStrategyLineAsync(page, IncompleteJobKeys[i], previousLength);
            previousLength = IncompleteJobKeys[i].Length;
            await page.WaitForTimeoutAsync(700);
            await Assert.That(await IsRuntimeAliveAsync(page)).IsTrue()
                .Because($"runtime died after typing '{IncompleteJobKeys[i]}'");
        }

        await ReplaceStrategyLineAsync(page, "strategy:\n      fail-fast: false", previousLength);
        await page.WaitForTimeoutAsync(700);
        await Assert.That(await IsRuntimeAliveAsync(page)).IsTrue();

        var completedYaml = await GetEditorWorkflowBaseAsync(page);
        var completedResult = await RunLintViaHooksAsync(page, completedYaml);
        await Assert.That(completedResult.Ok).IsTrue().Because(completedResult.Error ?? "unknown");
        await Assert.That(completedResult.Deferred).IsFalse();
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

    private static async Task SetEditorValueAsync(IPage page, string yaml)
    {
        await page.EvaluateAsync(
            """
            (source) => {
              const cm = document.querySelector('#editor .CodeMirror')?.CodeMirror;
              if (!cm) throw new Error('workflow editor missing');
              cm.setValue(source);
            }
            """,
            yaml);
    }

    private static async Task ReplaceStrategyLineAsync(IPage page, string fragment, int previousLength)
    {
        await page.EvaluateAsync(
            """
            ({ fragment, previousLength }) => {
              const cm = document.querySelector('#editor .CodeMirror')?.CodeMirror;
              if (!cm) throw new Error('workflow editor missing');
              cm.replaceRange(
                fragment,
                { line: 8, ch: 4 },
                { line: 8, ch: 4 + previousLength },
                '+input');
            }
            """,
            new { fragment, previousLength });
    }

    private static async Task<bool> IsRuntimeAliveAsync(IPage page)
    {
        return await page.EvaluateAsync<bool>(
            "() => globalThis.__SEITON_PLAYGROUND_TEST__?.getRuntimeAlive?.() !== false");
    }

    private static async Task AssertPageResponsiveAsync(IPage page, string operation)
    {
        var heartbeat = page.EvaluateAsync<bool>(
            "() => globalThis.__SEITON_PLAYGROUND_TEST__?.getRuntimeAlive?.() !== false");
        await CompleteWithinAsync(heartbeat, operation);
        await Assert.That(await heartbeat).IsTrue().Because(operation);
    }

    private static async Task CompleteWithinAsync(Task task, string operation)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That((object)completed).IsSameReferenceAs(task)
            .Because($"the page became unresponsive while {operation}");
        await task;
    }

    private static async Task<LintHookResult> RunLintViaHooksAsync(IPage page, string yaml)
    {
        return await page.EvaluateAsync<LintHookResult>(
            """
            (src) => globalThis.__SEITON_PLAYGROUND_TEST__.runLint(src, '.github/workflows/ci.yml')
            """,
            yaml);
    }

    private static async Task<LintHookResult> RunLintWithinAsync(IPage page, string yaml, string operation)
    {
        var lint = RunLintViaHooksAsync(page, yaml);
        await CompleteWithinAsync(lint, operation);
        return await lint;
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
