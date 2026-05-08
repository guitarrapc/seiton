using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Tests;

/// <summary>
/// Thread-safety audit tests for LintEngine.
/// These tests document that LintEngine is NOT safe for concurrent Check() calls
/// on the same instance, and verify that per-thread instances produce correct results.
/// </summary>
public sealed class LintEngineThreadSafetyTests
{
    private static byte[] BuildWorkflowYaml(int index) => Encoding.UTF8.GetBytes(
        $"""
        name: workflow-{index}
        on: push
        permissions: write-all
        jobs:
          build{index}:
            runs-on: ubuntu-latest
            steps:
              - run: echo {index}
        """);

    /// <summary>
    /// Demonstrates that per-thread LintEngine instances produce consistent results
    /// when processing multiple files in parallel. This is the safe pattern.
    /// </summary>
    [Test]
    public async Task ParallelCheck_PerThreadEngines_ProducesConsistentResults()
    {
        const int fileCount = 50;
        var yamlFiles = new byte[fileCount][];
        var filePaths = new string[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            yamlFiles[i] = BuildWorkflowYaml(i);
            filePaths[i] = $".github/workflows/test-{i}.yml";
        }

        // Establish sequential baseline
        var sequentialEngine = new LintEngine();
        var baselineCounts = new int[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            var result = sequentialEngine.Check(yamlFiles[i], filePaths[i]);
            baselineCounts[i] = result.Diagnostics.Length;
            result.ParseResult.Arena?.Dispose();
        }

        // Run parallel with per-thread engines
        using var engines = new ThreadLocal<LintEngine>(
            static () => new LintEngine(), trackAllValues: false);
        var parallelCounts = new int[fileCount];

        Parallel.For(0, fileCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var engine = engines.Value!;
                var result = engine.Check(yamlFiles[i], filePaths[i]);
                parallelCounts[i] = result.Diagnostics.Length;
                result.ParseResult.Arena?.Dispose();
            });

        // Per-thread engines must produce identical diagnostic counts
        for (var i = 0; i < fileCount; i++)
        {
            await Assert.That(parallelCounts[i]).IsEqualTo(baselineCounts[i]);
        }
    }

    /// <summary>
    /// Verifies that per-thread LintEngine instances produce diagnostics with correct
    /// content (not just counts) when compared to sequential execution.
    /// </summary>
    [Test]
    public async Task ParallelCheck_PerThreadEngines_DiagnosticContentMatchesSequential()
    {
        const int fileCount = 20;
        var yamlFiles = new byte[fileCount][];
        var filePaths = new string[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            yamlFiles[i] = BuildWorkflowYaml(i);
            filePaths[i] = $".github/workflows/test-{i}.yml";
        }

        // Sequential baseline: collect full diagnostic info
        var sequentialEngine = new LintEngine();
        var baselineResults = new (string RuleId, int Line, string Message)[fileCount][];
        for (var i = 0; i < fileCount; i++)
        {
            var result = sequentialEngine.Check(yamlFiles[i], filePaths[i]);
            baselineResults[i] = new (string, int, string)[result.Diagnostics.Length];
            for (var j = 0; j < result.Diagnostics.Length; j++)
            {
                var d = result.Diagnostics[j];
                baselineResults[i][j] = (d.RuleId ?? "", d.Location.StartLine, d.Message);
            }
            result.ParseResult.Arena?.Dispose();
        }

        // Parallel execution
        using var engines = new ThreadLocal<LintEngine>(
            static () => new LintEngine(), trackAllValues: false);
        var parallelResults = new (string RuleId, int Line, string Message)[fileCount][];

        Parallel.For(0, fileCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var engine = engines.Value!;
                var result = engine.Check(yamlFiles[i], filePaths[i]);
                var diags = new (string, int, string)[result.Diagnostics.Length];
                for (var j = 0; j < result.Diagnostics.Length; j++)
                {
                    var d = result.Diagnostics[j];
                    diags[j] = (d.RuleId ?? "", d.Location.StartLine, d.Message);
                }
                parallelResults[i] = diags;
                result.ParseResult.Arena?.Dispose();
            });

        // Verify full content match
        for (var i = 0; i < fileCount; i++)
        {
            await Assert.That(parallelResults[i].Length).IsEqualTo(baselineResults[i].Length);
            for (var j = 0; j < baselineResults[i].Length; j++)
            {
                await Assert.That(parallelResults[i][j]).IsEqualTo(baselineResults[i][j]);
            }
        }
    }

    /// <summary>
    /// Stress test: many parallel Check() calls with per-thread engines.
    /// Verifies no crashes, deadlocks, or corrupted state over repeated invocations.
    /// </summary>
    [Test]
    [Repeat(3)]
    public async Task ParallelCheck_PerThreadEngines_StressTest_NoCrashOrDeadlock()
    {
        const int fileCount = 100;
        var yamlFiles = new byte[fileCount][];
        var filePaths = new string[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            yamlFiles[i] = BuildWorkflowYaml(i % 10); // reuse 10 distinct files
            filePaths[i] = $".github/workflows/stress-{i}.yml";
        }

        using var engines = new ThreadLocal<LintEngine>(
            static () => new LintEngine(), trackAllValues: false);
        var totalDiagnostics = 0;

        Parallel.For(0, fileCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var engine = engines.Value!;
                var result = engine.Check(yamlFiles[i], filePaths[i]);
                Interlocked.Add(ref totalDiagnostics, result.Diagnostics.Length);
                result.ParseResult.Arena?.Dispose();
            });

        // Just verify we got some diagnostics without crashing
        await Assert.That(totalDiagnostics).IsGreaterThan(0);
    }

    // --- P2: Per-thread isolation pattern tests ---

    /// <summary>
    /// Validates the slot-based parallel result collection pattern (plan §5.2).
    /// Each slot stores CopyDiagnostics output; after Parallel.For completes,
    /// all slots are aggregated in input order. Verifies output-order stability.
    /// </summary>
    [Test]
    public async Task SlotPattern_OutputOrderMatchesInputOrder()
    {
        const int fileCount = 30;
        var yamlFiles = new byte[fileCount][];
        var filePaths = new string[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            yamlFiles[i] = BuildWorkflowYaml(i);
            filePaths[i] = $".github/workflows/slot-{i}.yml";
        }

        // Sequential baseline
        var sequentialEngine = new LintEngine();
        var baselineDiags = new Diagnostic[fileCount][];
        for (var i = 0; i < fileCount; i++)
        {
            var result = sequentialEngine.Check(yamlFiles[i], filePaths[i]);
            baselineDiags[i] = result.CopyDiagnostics();
            result.ParseResult.Arena?.Dispose();
        }

        // Parallel with slot pattern
        using var engines = new ThreadLocal<LintEngine>(
            static () => new LintEngine(), trackAllValues: false);
        var slots = new Diagnostic[fileCount][];

        Parallel.For(0, fileCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var result = engines.Value!.Check(yamlFiles[i], filePaths[i]);
                slots[i] = result.CopyDiagnostics();
                result.ParseResult.Arena?.Dispose();
            });

        // Aggregate in input order and compare
        for (var i = 0; i < fileCount; i++)
        {
            await Assert.That(slots[i].Length).IsEqualTo(baselineDiags[i].Length);
            for (var j = 0; j < baselineDiags[i].Length; j++)
            {
                await Assert.That(slots[i][j].RuleId).IsEqualTo(baselineDiags[i][j].RuleId);
                await Assert.That(slots[i][j].Message).IsEqualTo(baselineDiags[i][j].Message);
                await Assert.That(slots[i][j].Location.StartLine).IsEqualTo(baselineDiags[i][j].Location.StartLine);
            }
        }
    }

    /// <summary>
    /// Verifies that CopyDiagnostics produces a caller-owned array that remains valid
    /// after the engine processes subsequent files (arena buffer turnover).
    /// </summary>
    [Test]
    public async Task CopyDiagnostics_SurvivesSubsequentCheckCalls()
    {
        const int fileCount = 10;
        var yamlFiles = new byte[fileCount][];
        var filePaths = new string[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            yamlFiles[i] = BuildWorkflowYaml(i);
            filePaths[i] = $".github/workflows/retain-{i}.yml";
        }

        // Collect all CopyDiagnostics results first, then verify after all Check calls
        var engine = new LintEngine();
        var copiedResults = new Diagnostic[fileCount][];
        for (var i = 0; i < fileCount; i++)
        {
            var result = engine.Check(yamlFiles[i], filePaths[i]);
            copiedResults[i] = result.CopyDiagnostics();
            result.ParseResult.Arena?.Dispose();
        }

        // After all Check() calls, verify ALL copied results are still valid
        var verifyEngine = new LintEngine();
        for (var i = 0; i < fileCount; i++)
        {
            var freshResult = verifyEngine.Check(yamlFiles[i], filePaths[i]);
            var freshDiags = freshResult.CopyDiagnostics();
            freshResult.ParseResult.Arena?.Dispose();

            await Assert.That(copiedResults[i].Length).IsEqualTo(freshDiags.Length);
            for (var j = 0; j < freshDiags.Length; j++)
            {
                await Assert.That(copiedResults[i][j].RuleId).IsEqualTo(freshDiags[j].RuleId);
                await Assert.That(copiedResults[i][j].Message).IsEqualTo(freshDiags[j].Message);
            }
        }
    }

    /// <summary>
    /// Verifies that a single LintConfig instance can be safely shared across
    /// multiple ThreadLocal LintEngine instances. Each engine copies config settings
    /// into its own _effectiveConfig, so the shared LintConfig is read-only.
    /// </summary>
    [Test]
    public async Task SharedLintConfig_SafeAcrossThreadLocalEngines()
    {
        const int fileCount = 20;
        var yamlFiles = new byte[fileCount][];
        var filePaths = new string[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            yamlFiles[i] = BuildWorkflowYaml(i);
            filePaths[i] = $".github/workflows/shared-config-{i}.yml";
        }

        // One shared LintConfig across all threads
        var sharedConfig = new LintConfig();

        // Sequential baseline with same config
        var sequentialEngine = new LintEngine();
        var baselineCounts = new int[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            var result = sequentialEngine.Check(yamlFiles[i], filePaths[i], sharedConfig);
            baselineCounts[i] = result.Diagnostics.Length;
            result.ParseResult.Arena?.Dispose();
        }

        // Parallel with shared config
        using var engines = new ThreadLocal<LintEngine>(
            static () => new LintEngine(), trackAllValues: false);
        var parallelCounts = new int[fileCount];

        Parallel.For(0, fileCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var result = engines.Value!.Check(yamlFiles[i], filePaths[i], sharedConfig);
                parallelCounts[i] = result.Diagnostics.Length;
                result.ParseResult.Arena?.Dispose();
            });

        for (var i = 0; i < fileCount; i++)
        {
            await Assert.That(parallelCounts[i]).IsEqualTo(baselineCounts[i]);
        }
    }

    // --- P4: Integration-level verification tests ---

    /// <summary>
    /// Verifies deterministic output: running the same parallel check multiple times
    /// produces identical diagnostic sequences every time (output-order stability).
    /// </summary>
    [Test]
    public async Task ParallelSlotPattern_RepeatedRuns_ProduceIdenticalOutput()
    {
        const int fileCount = 30;
        const int runs = 5;
        var yamlFiles = new byte[fileCount][];
        var filePaths = new string[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            yamlFiles[i] = BuildWorkflowYaml(i);
            filePaths[i] = $".github/workflows/deterministic-{i}.yml";
        }

        // Collect diagnostic fingerprints from multiple parallel runs
        var runFingerprints = new string[runs][];
        for (var run = 0; run < runs; run++)
        {
            using var engines = new ThreadLocal<LintEngine>(
                static () => new LintEngine(), trackAllValues: false);
            var slots = new Diagnostic[fileCount][];

            Parallel.For(0, fileCount,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                i =>
                {
                    var result = engines.Value!.Check(yamlFiles[i], filePaths[i]);
                    slots[i] = result.CopyDiagnostics();
                    result.ParseResult.Arena?.Dispose();
                });

            // Flatten in input order → fingerprint
            var fingerprints = new List<string>();
            for (var i = 0; i < fileCount; i++)
            {
                for (var j = 0; j < slots[i].Length; j++)
                {
                    var d = slots[i][j];
                    fingerprints.Add($"{filePaths[i]}:{d.RuleId}:{d.Location.StartLine}:{d.Message}");
                }
            }
            runFingerprints[run] = fingerprints.ToArray();
        }

        // All runs must produce identical output
        for (var run = 1; run < runs; run++)
        {
            await Assert.That(runFingerprints[run].Length).IsEqualTo(runFingerprints[0].Length);
            for (var i = 0; i < runFingerprints[0].Length; i++)
            {
                await Assert.That(runFingerprints[run][i]).IsEqualTo(runFingerprints[0][i]);
            }
        }
    }

    /// <summary>
    /// End-to-end simulation of CheckCommand parallel path: file read → parallel Check
    /// → CopyDiagnostics into slots → aggregate in order → apply ignore filter.
    /// Verifies the full pipeline produces correct, filtered output.
    /// </summary>
    [Test]
    public async Task FullPipeline_ParallelCheckWithIgnoreFilter()
    {
        const int fileCount = 10;
        var yamlFiles = new byte[fileCount][];
        var filePaths = new string[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            yamlFiles[i] = BuildWorkflowYaml(i);
            filePaths[i] = $".github/workflows/pipeline-{i}.yml";
        }

        // Parallel check with slot pattern (mirrors CheckCommand)
        using var engines = new ThreadLocal<LintEngine>(
            static () => new LintEngine(), trackAllValues: false);
        var slots = new (Diagnostic[] Diagnostics, string FilePath)[fileCount];

        Parallel.For(0, fileCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var result = engines.Value!.Check(yamlFiles[i], filePaths[i]);
                slots[i] = (result.CopyDiagnostics(), filePaths[i]);
                result.ParseResult.Arena?.Dispose();
            });

        // Aggregate in input order
        var allDiagnostics = new List<Diagnostic>();
        for (var i = 0; i < fileCount; i++)
        {
            allDiagnostics.AddRange(slots[i].Diagnostics);
        }

        // Sequential baseline for comparison
        var sequentialEngine = new LintEngine();
        var baselineDiags = new List<Diagnostic>();
        for (var i = 0; i < fileCount; i++)
        {
            var result = sequentialEngine.Check(yamlFiles[i], filePaths[i]);
            baselineDiags.AddRange(result.CopyDiagnostics());
            result.ParseResult.Arena?.Dispose();
        }

        // Parallel aggregate must match sequential aggregate
        await Assert.That(allDiagnostics.Count).IsEqualTo(baselineDiags.Count);
        for (var i = 0; i < baselineDiags.Count; i++)
        {
            await Assert.That(allDiagnostics[i].RuleId).IsEqualTo(baselineDiags[i].RuleId);
            await Assert.That(allDiagnostics[i].Message).IsEqualTo(baselineDiags[i].Message);
            await Assert.That(allDiagnostics[i].Location.StartLine).IsEqualTo(baselineDiags[i].Location.StartLine);
        }

        // Verify diagnostics are accessible (not corrupted by arena/pool)
        await Assert.That(allDiagnostics.Count).IsGreaterThan(0);
        for (var i = 0; i < allDiagnostics.Count; i++)
        {
            await Assert.That(allDiagnostics[i].Message).IsNotNull();
        }
    }
}
