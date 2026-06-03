using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Reports;
using Seiton.Core.Linting;
using System.Text;

namespace Seiton.Benchmark;

internal sealed class PeakMemoryBenchmarkConfig : ManualConfig
{
    public PeakMemoryBenchmarkConfig()
    {
        AddColumn(new PeakHeapColumn());
        AddDiagnoser(MemoryDiagnoser.Default);
        HideColumns("Mean", "Error", "StdDev", "Ratio", "RatioSD", "Rank", "Gen0", "Gen1", "Gen2", "Allocated", "Alloc Ratio");
    }
}

/// <summary>
/// Reports peak heap delta (bytes) from benchmarks that return <see cref="long"/>.
/// </summary>
internal sealed class PeakHeapColumn : IColumn
{
    public string Id => nameof(PeakHeapColumn);
    public string ColumnName => "PeakHeap";
    public string Legend => "Peak live managed heap delta (bytes)";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Size;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        Format(summary, benchmarkCase);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
        Format(summary, benchmarkCase);

    public bool IsAvailable(Summary summary) => true;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    private static string Format(Summary summary, BenchmarkCase benchmarkCase)
    {
        var report = summary.Reports.FirstOrDefault(r => r.BenchmarkCase.Equals(benchmarkCase));
        if (report?.ResultStatistics is not { } stats)
        {
            return "NA";
        }

        return stats.Mean.ToString("N0");
    }
}

/// <summary>
/// Measures peak live managed heap during multi-file lint.
/// Unlike <see cref="MultiFileLintBenchmark"/> (which reports cumulative <c>Allocated</c>),
/// this benchmark reports <c>PeakHeap</c> (bytes above pre-iteration baseline).
/// </summary>
[Config(typeof(PeakMemoryBenchmarkConfig))]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class MultiFileLintPeakMemoryBenchmark
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
    private RetainedMemoryProbe.PeakSampler _sampler = null!;

    [GlobalSetup]
    public void Setup()
    {
        var n = (int)Count;
        _yamlFiles = new byte[n][];
        _filePaths = new string[n];

        for (var i = 0; i < n; i++)
        {
            var yaml = WorkflowYamlBuilder.Build(
                jobCount: 6, stepsPerJob: 8,
                nameSuffix: $"-file{i}");
            _yamlFiles[i] = Encoding.UTF8.GetBytes(yaml);
            _filePaths[i] = $".github/workflows/bench-{i}.yml";
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        RetainedMemoryProbe.CompactHeap();
        var baseline = RetainedMemoryProbe.GetLiveHeapBytes(forceFullCollection: true);
        _sampler = new RetainedMemoryProbe.PeakSampler(baseline);
    }

    /// <summary>Peak live heap delta for sequential lint (bytes above pre-run baseline).</summary>
    [Benchmark(Baseline = true, Description = "Sequential peak heap delta")]
    public long SequentialPeakHeapDeltaBytes()
    {
        MultiFileLintHarness.CheckSequential(_yamlFiles, _filePaths, _sampler);
        _sampler.Record();
        return _sampler.PeakDeltaBytes;
    }

    /// <summary>Peak live heap delta for parallel lint (bytes above pre-run baseline).</summary>
    [Benchmark(Description = "Parallel peak heap delta")]
    public long ParallelPeakHeapDeltaBytes()
    {
        MultiFileLintHarness.CheckParallel(_yamlFiles, _filePaths, _sampler);
        _sampler.Record();
        return _sampler.PeakDeltaBytes;
    }
}
