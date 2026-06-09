using System.Text;

namespace Seiton.Benchmark;

/// <summary>
/// Measures multi-file lint throughput and cumulative allocation for sequential vs parallel execution.
/// For peak live heap (retained memory), use <see cref="MultiFileLintPeakMemoryBenchmark"/>.
/// Used as baseline (P0) and comparison target (P4) for parallel check implementation.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class MultiFileLintBenchmark
{
    public enum FileCount
    {
        F1 = 1,
        F10 = 10,
        F50 = 50,
    }

    [Params(FileCount.F1, FileCount.F10, FileCount.F50)]
    public FileCount Count { get; set; }

    private byte[][] _yamlFiles = [];
    private string[] _filePaths = [];

    [GlobalSetup]
    public void Setup()
    {
        var n = (int)Count;
        _yamlFiles = new byte[n][];
        _filePaths = new string[n];

        for (var i = 0; i < n; i++)
        {
            // Each file is Medium size (6 jobs × 8 steps) with distinct content
            var yaml = WorkflowYamlBuilder.Build(
                jobCount: 6, stepsPerJob: 8,
                nameSuffix: $"-file{i}");
            _yamlFiles[i] = Encoding.UTF8.GetBytes(yaml);
            _filePaths[i] = $".github/workflows/bench-{i}.yml";
        }
    }

    /// <summary>Sequential path: single engine, for loop (current implementation equivalent)</summary>
    [Benchmark(Baseline = true, Description = "Sequential (for loop)")]
    public int CheckSequential() =>
        MultiFileLintHarness.CheckSequential(_yamlFiles, _filePaths);

    /// <summary>Parallel path: ThreadLocal + Parallel.For (post-parallelization target)</summary>
    [Benchmark(Description = "Parallel (ThreadLocal)")]
    public int CheckParallel() =>
        MultiFileLintHarness.CheckParallel(_yamlFiles, _filePaths);
}
