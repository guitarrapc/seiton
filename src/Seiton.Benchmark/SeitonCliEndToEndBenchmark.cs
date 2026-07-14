using System.Globalization;
using System.Text;
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
    private static string BuildCliBenchmarkWorkflow(int index)
    {
        var sb = new StringBuilder(capacity: 12_288);
        sb.Append("name: bench-cli-").AppendLine(index.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("run-name: Bench ${{ github.ref_name }}");
        sb.AppendLine("on:");
        sb.AppendLine("  push:");
        sb.AppendLine("    branches: [main, release/**]");
        sb.AppendLine("  pull_request_target:");
        sb.AppendLine("    types: [opened, synchronize]");
        sb.AppendLine("  workflow_dispatch:");
        sb.AppendLine("    inputs:");
        sb.AppendLine("      target:");
        sb.AppendLine("        type: choice");
        sb.AppendLine("        options: [dev, prod]");
        sb.AppendLine("        default: dev");
        sb.AppendLine("permissions:");
        sb.AppendLine("  contents: read");
        sb.AppendLine("env:");
        sb.AppendLine("  GLOBAL: value");
        sb.AppendLine("defaults:");
        sb.AppendLine("  run:");
        sb.AppendLine("    shell: bash");
        sb.AppendLine("concurrency:");
        sb.AppendLine("  group: bench-${{ github.ref }}");
        sb.AppendLine("  cancel-in-progress: true");
        sb.AppendLine("jobs:");
        sb.AppendLine("  build:");
        sb.AppendLine("    name: Build");
        sb.AppendLine("    runs-on: ubuntu-latest");
        sb.AppendLine("    container: node:20");
        sb.AppendLine("    services:");
        sb.AppendLine("      redis:");
        sb.AppendLine("        image: redis:7");
        sb.AppendLine("    strategy:");
        sb.AppendLine("      fail-fast: true");
        sb.AppendLine("      max-parallel: 2");
        sb.AppendLine("      matrix:");
        sb.AppendLine("        os: [ubuntu-latest, windows-latest]");
        sb.AppendLine("    env:");
        sb.AppendLine("      NPM_TOKEN: ${{ secrets.NPM_TOKEN }}");
        sb.AppendLine("    steps:");
        sb.AppendLine("      - name: Checkout");
        sb.AppendLine("        uses: actions/checkout@v4");
        sb.AppendLine("        with:");
        sb.AppendLine("          fetch-depth: '0'");
        sb.AppendLine("      - name: Setup Node");
        sb.AppendLine("        uses: actions/setup-node@v4");
        sb.AppendLine("        with:");
        sb.AppendLine("          node_version: '22'");
        sb.AppendLine("          cache: npm");
        sb.AppendLine("      - name: Cache dependencies");
        sb.AppendLine("        uses: actions/cache@v4");
        sb.AppendLine("        with:");
        sb.AppendLine("          path: ~/.npm");
        sb.AppendLine("          key: npm-${{ github.event.pull_request.title }}");
        sb.AppendLine("      - name: Deprecated output and template");
        sb.AppendLine("        run: |");
        sb.AppendLine("          echo \"::set-output name=title::${{ github.event.pull_request.title }}\"");
        sb.AppendLine("          echo \"$NPM_TOKEN\"");
        sb.AppendLine("      - name: Docker action");
        sb.AppendLine("        uses: docker://alpine:3.20");
        sb.AppendLine("  test:");
        sb.AppendLine("    name: Test");
        sb.AppendLine("    needs: build");
        sb.AppendLine("    runs-on: ubuntu-24.04");
        sb.AppendLine("    timeout-minutes: 20");
        sb.AppendLine("    permissions:");
        sb.AppendLine("      contents: read");
        sb.AppendLine("    steps:");
        sb.AppendLine("      - name: GitHub Script");
        sb.AppendLine("        uses: actions/github-script@v7");
        sb.AppendLine("        with:");
        sb.AppendLine("          script: |");
        sb.AppendLine("            core.info(\"${{ github.event.pull_request.title }}\")");
        sb.AppendLine("      - name: Test");
        sb.AppendLine("        run: npm test");
        sb.AppendLine("  deploy:");
        sb.AppendLine("    name: Deploy");
        sb.AppendLine("    needs: test");
        sb.AppendLine("    uses: acme/platform/.github/workflows/deploy.yml@main");
        sb.AppendLine("    secrets: inherit");
        return sb.ToString().Replace("\r\n", "\n");
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
