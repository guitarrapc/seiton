using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Parameters;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Seiton.Core.Linting;
using System.Text;

namespace Seiton.Benchmark;

internal sealed class PeakMemoryBenchmarkConfig : ManualConfig
{
    public PeakMemoryBenchmarkConfig()
    {
        AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
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
        if (!PeakHeapRecorder.TryGet(benchmarkCase, out var peakBytes))
        {
            return "NA";
        }

        return peakBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal static class PeakHeapRecorder
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Values = [];

    public static void Record(string methodName, object? countValue, long peakBytes)
    {
        var key = BuildKey(methodName, countValue);
        lock (Gate)
        {
            if (Values.TryGetValue(key, out var current))
            {
                if (peakBytes > current)
                {
                    Values[key] = peakBytes;
                }
            }
            else
            {
                Values[key] = peakBytes;
            }
        }
    }

    public static bool TryGet(BenchmarkCase benchmarkCase, out long peakBytes)
    {
        var countParam = benchmarkCase.Parameters?.Items
            .FirstOrDefault(static p => p.Name == nameof(MultiFileLintPeakMemoryBenchmark.Count));
        var countValue = countParam?.Value;
        var key = BuildKey(benchmarkCase.Descriptor.WorkloadMethod.Name, countValue);
        lock (Gate)
        {
            return Values.TryGetValue(key, out peakBytes);
        }
    }

    private static string BuildKey(string methodName, object? countValue) =>
        $"{methodName}:{countValue?.ToString() ?? "NA"}";
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
        var peakBytes = _sampler.PeakDeltaBytes;
        PeakHeapRecorder.Record(nameof(SequentialPeakHeapDeltaBytes), Count, peakBytes);
        return peakBytes;
    }

    /// <summary>Peak live heap delta for parallel lint (bytes above pre-run baseline).</summary>
    [Benchmark(Description = "Parallel peak heap delta")]
    public long ParallelPeakHeapDeltaBytes()
    {
        MultiFileLintHarness.CheckParallel(_yamlFiles, _filePaths, _sampler);
        _sampler.Record();
        var peakBytes = _sampler.PeakDeltaBytes;
        PeakHeapRecorder.Record(nameof(ParallelPeakHeapDeltaBytes), Count, peakBytes);
        return peakBytes;
    }
}
