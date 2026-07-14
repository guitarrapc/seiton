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
            var yaml = WorkflowYamlBuilder.Build(jobCount: 3, stepsPerJob: 6, nameSuffix: $"-cli-{i}");
            File.WriteAllText(Path.Combine(_repositoryRoot, ".github", "workflows", $"workflow-{i:D2}.yml"), yaml);
        }

        File.WriteAllText(Path.Combine(_repositoryRoot, ".github", "seiton.yaml"), """
            exclusions:
              - file: .github/workflows/workflow-*.yml
                jobs:
                  - job0
                rules:
                  - unpinned-uses
            fix:
              defaults:
                job-timeout-minutes: 15
            """);
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

    [Benchmark(Baseline = true, Description = "seiton check (auto-discovery, text output)")]
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

    [Benchmark(Description = "seiton validate-config (auto-discovery)")]
    public int ValidateConfig()
        => RunInRepository(() => ValidateCommand.Run(
            config: null,
            verboseLevel: VerboseLevel.Off,
            baseDirectory: _repositoryRoot,
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
