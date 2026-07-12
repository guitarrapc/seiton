using Seiton.Core.Linting;

namespace Seiton.Benchmark;

/// <summary>
/// Sequential/parallel multi-file lint loops used by <see cref="MultiFileLintPeakMemoryBenchmark"/>.
/// </summary>
internal static class MultiFileLintHarness
{
    public static int CheckSequential(byte[][] yamlFiles, string[] filePaths, RetainedMemoryProbe.PeakSampler? sampler = null)
    {
        ArgumentNullException.ThrowIfNull(yamlFiles);
        ArgumentNullException.ThrowIfNull(filePaths);
        if (yamlFiles.Length != filePaths.Length)
        {
            throw new ArgumentException("yamlFiles and filePaths must have the same length.");
        }

        var engine = new LintEngine();
        var total = 0;
        for (var i = 0; i < yamlFiles.Length; i++)
        {
            var result = engine.CheckDirect(yamlFiles[i], filePaths[i], out var arena);
            total += result.Diagnostics.Length;
            arena?.Dispose();
            sampler?.Record();
        }

        return total;
    }

    public static int CheckParallel(
        byte[][] yamlFiles,
        string[] filePaths,
        RetainedMemoryProbe.PeakSampler? sampler = null,
        int? maxDegreeOfParallelism = null)
    {
        ArgumentNullException.ThrowIfNull(yamlFiles);
        ArgumentNullException.ThrowIfNull(filePaths);
        if (yamlFiles.Length != filePaths.Length)
        {
            throw new ArgumentException("yamlFiles and filePaths must have the same length.");
        }

        using var engines = new ThreadLocal<LintEngine>(
            static () => new LintEngine(), trackAllValues: false);
        var slots = new int[yamlFiles.Length];

        Parallel.For(
            0,
            yamlFiles.Length,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? Environment.ProcessorCount },
            i =>
            {
                var result = engines.Value!.CheckDirect(yamlFiles[i], filePaths[i], out var arena);
                slots[i] = result.Diagnostics.Length;
                arena?.Dispose();
                sampler?.Record();
            });

        var total = 0;
        for (var i = 0; i < slots.Length; i++)
        {
            total += slots[i];
        }

        return total;
    }
}
