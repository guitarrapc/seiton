using Seiton.Core.Linting;
using Seiton.Playground;

namespace Seiton.Benchmark;

/// <summary>
/// Measures per-call allocation of <see cref="PlaygroundLintRunner.RunToJsonUtf8"/> (Utf8JsonWriter path)
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public partial class PlaygroundLintBenchmark
{
    public enum WorkflowSize
    {
        Small,
        Large,
    }

    [Params(WorkflowSize.Small, WorkflowSize.Large)]
    public WorkflowSize Size { get; set; }

    private string _yamlSource = string.Empty;
    private const string FilePath = ".github/workflows/bench.yml";

    private LintEngine _engine = null!;
    private static readonly LintConfig BenchConfig = new()
    {
        Fix = new FixConfig { Enabled = true },
        Network = new NetworkConfig(),
        Output = new OutputConfig(),
        SkipSuppressionSummary = true,
    };

    [GlobalSetup]
    public void Setup()
    {
        _yamlSource = Size switch
        {
            WorkflowSize.Small => WorkflowYamlBuilder.Build(jobCount: 1, stepsPerJob: 3),
            WorkflowSize.Large => WorkflowYamlBuilder.Build(jobCount: 6, stepsPerJob: 8),
            _ => WorkflowYamlBuilder.Build(jobCount: 1, stepsPerJob: 3),
        };
        _engine = new LintEngine();

        // Warm up both paths
        PlaygroundLintRunner.RunToJsonUtf8(_yamlSource, FilePath);
    }

    [Benchmark]
    public int RunToJson_10()
    {
        var totalLength = 0;
        for (var i = 0; i < 10; i++)
        {
            totalLength += PlaygroundLintRunner.RunToJsonUtf8(_yamlSource, FilePath).Length;
        }

        return totalLength;
    }


    [Benchmark]
    public int RunToJson_100()
    {
        var totalLength = 0;
        for (var i = 0; i < 100; i++)
        {
            totalLength += PlaygroundLintRunner.RunToJsonUtf8(_yamlSource, FilePath).Length;
        }

        return totalLength;
    }
}
