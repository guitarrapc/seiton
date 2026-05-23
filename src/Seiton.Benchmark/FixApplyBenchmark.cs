using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Benchmark;

/// <summary>
/// Benchmarks the full fix application loop including conflict-aware batch selection
/// and iterative relinting. Measures the overhead of the iterative approach vs
/// a hypothetical single-pass apply.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class FixApplyBenchmark
{
    public enum Scenario
    {
        /// <summary>Single job missing permissions + timeout (conflicting inserts at same offset).</summary>
        SingleJobConflict,
        /// <summary>Multiple jobs each missing permissions + timeout (multiple conflicts).</summary>
        MultiJobConflict,
        /// <summary>No conflicts — all fixes at distinct offsets.</summary>
        NoConflict,
    }

    [Params(Scenario.SingleJobConflict, Scenario.MultiJobConflict, Scenario.NoConflict)]
    public Scenario TestScenario { get; set; }

    private byte[] _yamlBytes = [];
    private string _filePath = string.Empty;
    private LintEngine _engine = null!;
    private LintConfig _lintConfig = null!;

    [GlobalSetup]
    public void Setup()
    {
        var yaml = TestScenario switch
        {
            Scenario.SingleJobConflict => BuildSingleJobConflict(),
            Scenario.MultiJobConflict => BuildMultiJobConflict(),
            Scenario.NoConflict => BuildNoConflict(),
            _ => BuildSingleJobConflict(),
        };

        _yamlBytes = Encoding.UTF8.GetBytes(yaml);
        _filePath = $"bench-fix-{TestScenario.ToString().ToLowerInvariant()}.yml";
        _engine = new LintEngine();
        _lintConfig = new LintConfig
        {
            Fix = new FixConfig
            {
                Enabled = true,
                Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 15 }
            }
        };
    }

    [Benchmark(Description = "Iterative fix apply (conflict-aware)")]
    public byte[] ApplyFixesIteratively()
    {
        var currentYaml = _yamlBytes;
        const int maxPasses = 8;

        for (var pass = 0; pass < maxPasses; pass++)
        {
            using var handle = _engine.Check(currentYaml, _filePath, _lintConfig);
            if (!handle.HasFixableDiagnostics)
                break;

            var fixable = handle.FixableDiagnostics;
            var batch = SelectNonConflictingBatch(fixable);
            var nextYaml = FixEngine.Apply(currentYaml, batch);
            if (nextYaml.AsSpan().SequenceEqual(currentYaml))
                break;

            currentYaml = nextYaml;
        }

        return currentYaml;
    }

    /// <summary>Mirrors the SelectNonConflictingBatch logic from FixCommand.</summary>
    private static Diagnostic[] SelectNonConflictingBatch(Diagnostic[] fixableDiagnostics)
    {
        if (fixableDiagnostics.Length <= 1)
            return fixableDiagnostics;

        var diagRanges = new (int minOffset, int diagIndex)[fixableDiagnostics.Length];
        for (var i = 0; i < fixableDiagnostics.Length; i++)
        {
            var fix = fixableDiagnostics[i].Fix!.Value;
            var minOff = int.MaxValue;
            for (var j = 0; j < fix.Edits.Length; j++)
            {
                if (fix.Edits[j].Offset < minOff)
                    minOff = fix.Edits[j].Offset;
            }
            diagRanges[i] = (minOff, i);
        }

        Array.Sort(diagRanges, static (a, b) => a.minOffset.CompareTo(b.minOffset));

        var occupiedCount = 0;
        var occupied = new (int offset, int end)[fixableDiagnostics.Length * 2];
        var selected = new List<int>(fixableDiagnostics.Length);

        for (var i = 0; i < diagRanges.Length; i++)
        {
            var diagIdx = diagRanges[i].diagIndex;
            var fix = fixableDiagnostics[diagIdx].Fix!.Value;

            var conflicts = false;
            for (var j = 0; j < fix.Edits.Length; j++)
            {
                var editOffset = fix.Edits[j].Offset;
                var editEnd = editOffset + fix.Edits[j].Length;

                for (var k = 0; k < occupiedCount; k++)
                {
                    if (editOffset == occupied[k].offset || editOffset < occupied[k].end ||
                        (editEnd > occupied[k].offset && editOffset < occupied[k].end))
                    {
                        conflicts = true;
                        break;
                    }
                }

                if (conflicts) break;
            }

            if (!conflicts)
            {
                selected.Add(diagIdx);
                for (var j = 0; j < fix.Edits.Length; j++)
                {
                    var editOffset = fix.Edits[j].Offset;
                    var editEnd = editOffset + fix.Edits[j].Length;
                    if (editEnd == editOffset) editEnd = editOffset + 1;
                    occupied[occupiedCount++] = (editOffset, editEnd);
                }
            }
        }

        if (selected.Count == fixableDiagnostics.Length)
            return fixableDiagnostics;

        var result = new Diagnostic[selected.Count];
        for (var i = 0; i < selected.Count; i++)
            result[i] = fixableDiagnostics[selected[i]];

        return result;
    }

    private static string BuildSingleJobConflict()
    {
        return """
            on:
              pull_request:
                branches: [main]
            jobs:
              test:
                runs-on: ubuntu-24.04
                steps:
                  - run: echo "hello"
            """.Replace("\r\n", "\n");
    }

    private static string BuildMultiJobConflict()
    {
        var sb = new StringBuilder();
        sb.AppendLine("on: push");
        sb.AppendLine("jobs:");
        for (var i = 0; i < 5; i++)
        {
            sb.Append("  job").Append(i).AppendLine(":");
            sb.AppendLine("    runs-on: ubuntu-24.04");
            sb.AppendLine("    steps:");
            sb.AppendLine("      - run: echo hello");
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string BuildNoConflict()
    {
        // Workflow with permissions already set (no conflict), but missing timeout-minutes
        return """
            on: push
            jobs:
              test:
                runs-on: ubuntu-24.04
                permissions:
                  contents: read
                steps:
                  - run: echo "hello"
            """.Replace("\r\n", "\n");
    }
}
