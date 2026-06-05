using Microsoft.Playwright;

namespace Seiton.Playground.Tests;

/// <summary>
/// Reproduces Playground WASM memory/runtime crashes while incrementally typing a large workflow
/// (default sample → ~50-line job) with debounced lint, mimicking real keyboard input.
/// </summary>
[NotInParallel(PlaygroundUiTestHost.ParallelLockKey)]
public sealed class PlaygroundWasmMemoryCrashUiTests
{
    private const int DebounceMs = 500;
    private const int TypingSeed = 0x5E17_0600;

    /// <summary>Loaded editor content for <c>SAMPLES.default</c> (includes comment line).</summary>
    private const string DefaultSampleYaml = """
        # Paste your workflow YAML to this code editor

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

    /// <summary>~50-line job appended to the default workflow while typing.</summary>
    private const string DeployJobSuffix = """

          deploy:
            needs: test
            runs-on: ubuntu-24.04
            timeout-minutes: 30
            env:
              NODE_ENV: production
              DOTNET_NOLOGO: true
            strategy:
              fail-fast: false
              matrix:
                target: [web, api, worker]
                region: [eastus, westus, northeurope]
            steps:
              - name: Checkout
                uses: actions/checkout@v4
                with:
                  fetch-depth: 0
              - name: Setup Node
                uses: actions/setup-node@v4
                with:
                  node-version: 20.x
                  cache: npm
              - name: Setup .NET
                uses: actions/setup-dotnet@v4
                with:
                  dotnet-version: |
                    8.0.x
                    9.0.x
              - name: Restore cache
                uses: actions/cache@v4
                with:
                  path: |
                    ~/.npm
                    ~/.nuget/packages
                  key: ${{ matrix.target }}-${{ matrix.region }}-${{ hashFiles('**/package-lock.json', '**/*.csproj') }}
              - run: npm ci
              - run: npm run build -- --mode=${{ matrix.target }}
              - run: dotnet restore
              - run: dotnet build --configuration Release --no-restore
              - run: dotnet test --no-build --configuration Release --filter "FullyQualifiedName~Deploy"
              - run: docker build -t app:${{ github.sha }} .
              - run: echo "Deploying ${{ matrix.target }} to ${{ matrix.region }}"
        """;

    private const string FullFixConfig = """
        # NOTE: enable-network uses the GitHub API (unauthenticated, 60 req/hr limit).
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

    [Test]
    public async Task TypingIncrementalDeployJob_RepeatedEdits_DoNotCrashRuntime()
    {
        Skip.Test("Nop. We don't want to run this test currently because regression not happen & it takes too long.");

        var host = await PlaygroundUiTestHost.GetOrCreateAsync(PlaygroundWasmPublishMode.ReleaseAot);
        var browser = await PlaygroundUiBrowserSession.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1100, Height = 800 },
        });
        var (page, consoleErrors) = await OpenPlaygroundAsync(context, host.BaseUrl);
        await ApplyFullFixConfigViaHooksAsync(page);

        var chunks = BuildTypingChunks(DeployJobSuffix, new Random(TypingSeed));
        var failures = new List<string>();
        const int roundCount = 3;

        for (var round = 0; round < roundCount; round++)
        {
            await ResetEditorToDefaultAsync(page);
            await WaitForLintSettleAsync(page, delayMs: DebounceMs + 250);

            var builtSuffix = string.Empty;
            for (var i = 0; i < chunks.Length; i++)
            {
                builtSuffix += chunks[i];
                await TypeChunkViaEditorAsync(page, chunks[i]);
                await WaitForLintSettleAsync(page, delayMs: RandomTypingDelayMs(round, i));

                if (!await IsRuntimeAliveAsync(page))
                {
                    failures.Add($"round {round} chunk {i}/{chunks.Length}: runtime died after typing {builtSuffix.Length} suffix chars");
                    break;
                }

                if (await HasRuntimeCrashToastAsync(page))
                {
                    failures.Add($"round {round} chunk {i}/{chunks.Length}: crash toast visible");
                    break;
                }
            }

            if (failures.Count > 0)
            {
                break;
            }

            // Mid-edit churn: delete half the deploy job and re-type those chunks.
            var deleteCount = builtSuffix.Length / 2;
            var retypeChunks = BuildRetypeChunks(chunks, deleteCount);

            await DeleteSuffixCharsViaEditorAsync(page, deleteCount);
            await WaitForLintSettleAsync(page, delayMs: DebounceMs + 300);

            for (var i = 0; i < retypeChunks.Count; i++)
            {
                await TypeChunkViaEditorAsync(page, retypeChunks[i]);
                await WaitForLintSettleAsync(page, delayMs: RandomTypingDelayMs(round + 100, i));

                if (!await IsRuntimeAliveAsync(page))
                {
                    failures.Add($"round {round} retype chunk {i}: runtime died");
                    break;
                }
            }

            if (failures.Count > 0)
            {
                break;
            }
        }

        failures.AddRange(consoleErrors.Select(e => $"console: {e}"));

        await Assert.That(failures).IsEmpty()
            .Because($"Playground WASM runtime crashed during incremental typing:\n{string.Join('\n', failures)}");
    }

    /// <summary>
    /// Splits suffix into variable-size chunks; prefers line breaks, mimicking pauses while typing.
    /// </summary>
    internal static string[] BuildTypingChunks(string suffix, Random rng)
    {
        var chunks = new List<string>();
        var pos = 0;
        while (pos < suffix.Length)
        {
            var maxLen = rng.Next(4, 28);
            var end = Math.Min(pos + maxLen, suffix.Length);

            var nextNewline = suffix.IndexOf('\n', pos, end - pos);
            if (nextNewline >= 0 && nextNewline > pos)
            {
                end = nextNewline + 1;
            }
            else if (end < suffix.Length && char.IsWhiteSpace(suffix[end]))
            {
                while (end < suffix.Length && char.IsWhiteSpace(suffix[end]))
                {
                    end++;
                }
            }

            if (end <= pos)
            {
                end = Math.Min(pos + 1, suffix.Length);
            }

            chunks.Add(suffix[pos..end]);
            pos = end;
        }

        return chunks.ToArray();
    }

    internal static List<string> BuildRetypeChunks(string[] chunks, int deleteCount)
    {
        var retypeChunks = new List<string>();
        var acc = 0;
        foreach (var chunk in chunks)
        {
            var chunkStart = acc;
            acc += chunk.Length;
            if (acc <= deleteCount)
            {
                continue;
            }

            if (chunkStart >= deleteCount)
            {
                retypeChunks.Add(chunk);
            }
            else
            {
                retypeChunks.Add(chunk[(deleteCount - chunkStart)..]);
            }
        }

        return retypeChunks;
    }

    private static int RandomTypingDelayMs(int round, int chunkIndex)
    {
        // Deterministic but scattered: thinking pauses + debounce window.
        var rng = new Random(TypingSeed ^ (round * 997) ^ (chunkIndex * 37));
        return DebounceMs + rng.Next(80, 450);
    }

    private static async Task<(IPage Page, List<string> ConsoleErrors)> OpenPlaygroundAsync(IBrowserContext context, string baseUrl)
    {
        var page = await context.NewPageAsync();
        var consoleErrors = new List<string>();
        page.Console += (_, msg) =>
        {
            var text = msg.Text;
            if (text.Contains("memory access out of bounds", StringComparison.OrdinalIgnoreCase)
                || text.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                || text.Contains("allocation failed", StringComparison.OrdinalIgnoreCase)
                || text.Contains("runtime already exited", StringComparison.OrdinalIgnoreCase)
                || text.Contains("cannot enlarge memory arrays", StringComparison.OrdinalIgnoreCase)
                || text.Contains("abort(", StringComparison.OrdinalIgnoreCase))
            {
                consoleErrors.Add(text);
                Console.WriteLine($"[playground-console] {text}");
            }
        };

        await PlaygroundUiLayoutTests.GotoPlaygroundAndWaitForLinterGridAsync(
            page,
            $"{baseUrl.TrimEnd('/')}/?seitonTestHooks=1");

        await page.WaitForFunctionAsync(
            "() => typeof globalThis.__SEITON_PLAYGROUND_TEST__?.getRuntimeAlive === 'function'",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 120_000 });

        return (page, consoleErrors);
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

    private static async Task<bool> HasRuntimeCrashToastAsync(IPage page)
    {
        var crashToast = page.Locator("#toast-stack .toast--error", new PageLocatorOptions
        {
            HasText = "runtime has crashed",
        });
        return await crashToast.CountAsync() > 0;
    }

    private static async Task ResetEditorToDefaultAsync(IPage page)
    {
        await page.EvaluateAsync(
            """
            (yaml) => {
              const cm = document.querySelector('#editor .CodeMirror')?.CodeMirror;
              if (!cm) throw new Error('workflow editor missing');
              cm.setValue(yaml);
              cm.refresh();
            }
            """,
            DefaultSampleYaml);
    }

    private static async Task TypeChunkViaEditorAsync(IPage page, string chunk)
    {
        await page.EvaluateAsync(
            """
            (text) => {
              const cm = document.querySelector('#editor .CodeMirror')?.CodeMirror;
              if (!cm) throw new Error('workflow editor missing');
              const pos = cm.getCursor('end');
              cm.replaceRange(text, pos, pos, '+input');
            }
            """,
            chunk);
    }

    private static async Task DeleteSuffixCharsViaEditorAsync(IPage page, int charCount)
    {
        await page.EvaluateAsync(
            """
            (count) => {
              const cm = document.querySelector('#editor .CodeMirror')?.CodeMirror;
              if (!cm) throw new Error('workflow editor missing');
              const value = cm.getValue();
              const from = Math.max(0, value.length - count);
              const fromPos = cm.posFromIndex(from);
              const toPos = cm.posFromIndex(value.length);
              cm.replaceRange('', fromPos, toPos, '+input');
            }
            """,
            charCount);
    }

    private static async Task WaitForLintSettleAsync(IPage page, int delayMs)
    {
        await page.WaitForTimeoutAsync(delayMs);
    }

    private static async Task<bool> IsRuntimeAliveAsync(IPage page)
    {
        return await page.EvaluateAsync<bool>(
            "() => globalThis.__SEITON_PLAYGROUND_TEST__?.getRuntimeAlive?.() !== false");
    }

    private sealed class SetConfigHookResult
    {
        public object[] Diagnostics { get; set; } = [];
    }
}
