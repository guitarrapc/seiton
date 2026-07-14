using System.Globalization;
using BenchmarkDotNet.Order;
using Seiton.Cli;
using Seiton.Commands;
using Seiton.Output;

namespace Seiton.Benchmark;

/// <summary>
/// Measures the CLI command path after argument parsing: config discovery/load,
/// input discovery, file I/O, lint/config validation, output formatting, and summaries.
/// Process startup and ConsoleAppFramework argument binding are intentionally excluded.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SeitonCliEndToEndBenchmark
{
    public enum RepositorySize
    {
        SingleWorkflow,
        TenWorkflows,
    }

    [Params(RepositorySize.SingleWorkflow, RepositorySize.TenWorkflows)]
    public RepositorySize Size { get; set; }

    private string _repositoryRoot = string.Empty;
    private TextWriter _originalOut = TextWriter.Null;
    private TextWriter _originalError = TextWriter.Null;

    [GlobalSetup]
    public void Setup()
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);

        _repositoryRoot = Path.Combine(Path.GetTempPath(), "seiton-cli-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_repositoryRoot, ".github", "workflows"));
        Directory.CreateDirectory(Path.Combine(_repositoryRoot, ".github"));

        var workflowCount = Size == RepositorySize.SingleWorkflow ? 1 : 10;
        for (var i = 0; i < workflowCount; i++)
        {
            // Exercise realistic CLI diagnostic/fix/output paths, not only clean workflows.
            var yaml = BuildCliBenchmarkWorkflow(i);
            File.WriteAllText(Path.Combine(_repositoryRoot, ".github", "workflows", $"workflow-{i:D2}.yml"), yaml);
        }

        File.WriteAllText(Path.Combine(_repositoryRoot, ".github", "seiton.yaml"), """
            exclusions:
              - file: .github/workflows/workflow-*.yml
                jobs:
                  - build
                rules:
                  - unpinned-uses
            fix:
              defaults:
                job-timeout-minutes: 15
            """);
    }

    // Keep this fixture representative enough to exercise common CLI E2E paths and rule output.
    // Covered rules include dangerous-triggers, job-permissions-required, unpinned-image,
    // job-secrets, checkout-persist-credentials, popular-action-inputs, cache-poisoning-trigger,
    // deprecated-commands, template-injection, unpinned-uses, and deny-inherit-secrets.
    // The workflow also includes step output references plus parallel, background, wait,
    // and wait-all steps to cover parser/flow paths that do not necessarily emit diagnostics.
    private static string BuildCliBenchmarkWorkflow(int index)
    {
        return $$$"""
        name: bench-cli-{{{index.ToString(CultureInfo.InvariantCulture)}}}
        run-name: Bench ${{ github.ref_name }}
        on:
            push:
                branches: [main, release/**]
            pull_request_target:
                types: [opened, synchronize]
            workflow_dispatch:
                inputs:
                    target:
                        type: choice
                        options: [dev, prod]
                        default: dev
        permissions:
            contents: read
        env:
            GLOBAL: value
        defaults:
            run:
                shell: bash
        concurrency:
            group: bench-${{ github.ref }}
            cancel-in-progress: true
        jobs:
            build:
                name: Build
                runs-on: ubuntu-latest
                container: node:20
                services:
                    redis:
                        image: redis:7
                strategy:
                    fail-fast: true
                    max-parallel: 2
                    matrix:
                        os: [ubuntu-latest, windows-latest]
                env:
                    NPM_TOKEN: ${{ secrets.NPM_TOKEN }}
                steps:
                    - name: Checkout
                        uses: actions/checkout@v4
                        with:
                            fetch-depth: '0'
                    - name: Setup Node
                        uses: actions/setup-node@v4
                        with:
                            node_version: '22'
                            cache: npm
                    - name: Cache dependencies
                        uses: actions/cache@v4
                        with:
                            path: ~/.npm
                            key: npm-${{ github.event.pull_request.title }}
                    - id: build_meta
                        name: Deprecated output and template
                        run: |
                            echo "::set-output name=title::${{ github.event.pull_request.title }}"
                            echo "$NPM_TOKEN"
                    - name: Docker action
                        uses: docker://alpine:3.20
            test:
                name: Test
                needs: build
                runs-on: ubuntu-24.04
                timeout-minutes: 20
                permissions:
                    contents: read
                outputs:
                    artifact-version: ${{ steps.prepare.outputs.version }}
                steps:
                    - id: prepare
                        name: Prepare outputs
                        run: |
                            echo "version=1.2.3" >> "$GITHUB_OUTPUT"
                            echo "sha=${{ github.sha }}" >> "$GITHUB_OUTPUT"
                    - name: Parallel checks
                        parallel:
                            - id: lint_api
                                name: Lint API
                                run: echo "lint api ${{ steps.prepare.outputs.version }}"
                            - id: lint_web
                                name: Lint Web
                                run: echo "lint web ${{ steps.prepare.outputs.sha }}"
                    - name: Wait for parallel lint
                        wait: [lint_api, lint_web]
                    - id: docs_server
                        name: Start docs server
                        run: npm run docs:serve
                        background: true
                    - id: api_server
                        name: Start API server
                        run: npm run api:serve
                        background: true
                    - name: Wait for background servers
                        wait-all: null
                    - name: GitHub Script
                        uses: actions/github-script@v7
                        with:
                            script: |
                                core.info("${{ github.event.pull_request.title }}")
                    - name: Test
                        if: ${{ steps.prepare.outputs.version != '' }}
                        run: npm test -- --version "${{ steps.prepare.outputs.version }}"
            deploy:
                name: Deploy
                needs: test
                uses: acme/platform/.github/workflows/deploy.yml@main
                with:
                    version: ${{ needs.test.outputs.artifact-version }}
                secrets: inherit
        """.Replace("\r\n", "\n");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);

        if (Directory.Exists(_repositoryRoot))
        {
            Directory.Delete(_repositoryRoot, recursive: true);
        }
    }

    [Benchmark(Baseline = true, Description = "seiton check")]
    public int CheckText()
        => RunInRepository(static () => CheckCommand.Run(
            [],
            config: null,
            stdinFilename: "<stdin>",
            ignore: [],
            minSeverity: null,
            format: OutputFormat.Text,
            oneline: false,
            color: ColorMode.Never,
            noColor: true,
            verboseLevel: VerboseLevel.Off,
            includeActions: false,
            skipAgenticWorkflows: false,
            formatExplicitlySet: true));

    [Benchmark(Description = "seiton --fix --dry-run")]
    public int FixDryRunText()
        => RunInRepository(static () => FixCommand.RunAsync(
            [],
            config: null,
            stdinFilename: "<stdin>",
            ignore: [],
            minSeverity: null,
            format: OutputFormat.Text,
            oneline: false,
            color: ColorMode.Never,
            noColor: true,
            verboseLevel: VerboseLevel.Off,
            dryRun: true,
            check: false,
            enablePinNetwork: false,
            enableImageNetwork: false,
            includeActions: false,
            skipAgenticWorkflows: false,
            showDiff: false,
            formatExplicitlySet: true,
            output: TextWriter.Null,
            error: TextWriter.Null).GetAwaiter().GetResult());

    [Benchmark(Description = "seiton validate-config")]
    public int ValidateConfig()
        => RunInRepository(() => ValidateCommand.Run(
            config: null,
            verboseLevel: VerboseLevel.Off,
            baseDirectory: _repositoryRoot,
            output: TextWriter.Null,
            error: TextWriter.Null));

    [Benchmark(Description = "seiton rules")]
    public int RulesText()
        => RunInRepository(static () => RulesCommand.Run(
            config: null,
            format: OutputFormat.Text,
            output: TextWriter.Null,
            error: TextWriter.Null));

    private int RunInRepository(Func<int> action)
    {
        var currentDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _repositoryRoot;
        try
        {
            return action();
        }
        finally
        {
            Environment.CurrentDirectory = currentDirectory;
        }
    }
}
