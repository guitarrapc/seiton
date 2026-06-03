using Seiton.Playground;

namespace Seiton.Playground.Tests;

/// <summary>Desktop reproducers for WASM-only failures (bisect before browser tests).</summary>
[NotInParallel(PlaygroundTestParallelism.AssemblyLockKey)]
public sealed class PlaygroundWasmMemoryOobDesktopTests : IDisposable
{
    private const string FullFixConfig = """
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

    /// <summary>Matches <c>SAMPLES.default</c> in playground <c>main.js</c>.</summary>
    private const string DefaultSample = """
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

    public PlaygroundWasmMemoryOobDesktopTests() => PlaygroundLintRunner.SetConfig(FullFixConfig);

    public void Dispose() => PlaygroundLintRunner.SetConfig(null);

    [Test]
    public void RunLint_PartialUsesLine_DoesNotThrow()
    {
        var yaml = DefaultSample + "      - uses:";
        _ = PlaygroundLintRunner.RunToJsonUtf8(yaml, ".github/workflows/ci.yml");
    }

    [Test]
    public void RunLint_AppendTrailingVersionKeyWithoutSpace_DoesNotThrow()
    {
        var yaml = DefaultSample + """
              - uses: guitarrapc/setup-seiton@v1.0.0
                with:
                  version:
            """;
        _ = PlaygroundLintRunner.RunToJsonUtf8(yaml, ".github/workflows/ci.yml");
    }

    [Test]
    public void RunLint_IncompleteTrailingStep_DoesNotThrow()
    {
        var yaml = DefaultSample + """
              - uses: guitarrapc/setup-seiton@v1.0.0
                with:
                  version: 
            """;
        _ = PlaygroundLintRunner.RunToJsonUtf8(yaml, ".github/workflows/ci.yml");
    }
}
