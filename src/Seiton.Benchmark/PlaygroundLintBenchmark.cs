using Seiton.Core.Linting;
using Seiton.Playground;

namespace Seiton.Benchmark;

/// <summary>
/// Measures per-call allocation of <see cref="PlaygroundLintRunner.RunToJsonUtf8"/> (Utf8JsonWriter path).
/// Three scenarios:
/// <list type="bullet">
///   <item><description><c>NoChange</c> — same string object every call (reference-equality cache hit)</description></item>
///   <item><description><c>PartialChange</c> — only one job differs each call (typing-like edits; full parse + full lint)</description></item>
///   <item><description><c>FullChange</c> — entirely different content each call (full parse + full lint)</description></item>
/// </list>
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

    private const int Iterations = 10;

    [Params(WorkflowSize.Small, WorkflowSize.Large)]
    public WorkflowSize Size { get; set; }

    private string _yamlSource = string.Empty;
    private string[] _fullChangeYamls = [];
    private string[] _partialChangeYamls = [];
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
        var (jobCount, stepsPerJob) = Size switch
        {
            WorkflowSize.Small => (1, 3),
            WorkflowSize.Large => (6, 8),
            _ => (1, 3),
        };

        _yamlSource = WorkflowYamlBuilder.Build(jobCount: jobCount, stepsPerJob: stepsPerJob);

        // Full change: each variant has a different workflow name → all section hashes differ
        _fullChangeYamls = new string[Iterations];
        for (var i = 0; i < Iterations; i++)
        {
            _fullChangeYamls[i] = WorkflowYamlBuilder.Build(jobCount: jobCount, stepsPerJob: stepsPerJob, nameSuffix: $"-variant{i}");
        }

        // Partial change: only the first job's step name differs → other jobs can be skipped
        _partialChangeYamls = new string[Iterations];
        for (var i = 0; i < Iterations; i++)
        {
            _partialChangeYamls[i] = WorkflowYamlBuilder.Build(jobCount: jobCount, stepsPerJob: stepsPerJob, firstJobStepSuffix: $"-edit{i}");
        }

        _engine = new LintEngine();

        // Warm up
        PlaygroundLintRunner.RunToJsonUtf8(_yamlSource, FilePath);
    }

    /// <summary>Same string reference every call — exercises reference-equality cache.</summary>
    [Benchmark(Baseline = true)]
    public int NoChange()
    {
        var totalLength = 0;
        for (var i = 0; i < Iterations; i++)
        {
            totalLength += PlaygroundLintRunner.RunToJsonUtf8(_yamlSource, FilePath).Length;
        }

        return totalLength;
    }

    /// <summary>Only first job differs each call — typing-like edit pattern.</summary>
    [Benchmark]
    public int PartialChange()
    {
        var totalLength = 0;
        for (var i = 0; i < Iterations; i++)
        {
            totalLength += PlaygroundLintRunner.RunToJsonUtf8(_partialChangeYamls[i], FilePath).Length;
        }

        return totalLength;
    }

    /// <summary>Entirely different content each call — full parse + full lint every time.</summary>
    [Benchmark]
    public int FullChange()
    {
        var totalLength = 0;
        for (var i = 0; i < Iterations; i++)
        {
            totalLength += PlaygroundLintRunner.RunToJsonUtf8(_fullChangeYamls[i], FilePath).Length;
        }

        return totalLength;
    }
}
