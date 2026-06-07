using Seiton.Commands;
using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Benchmark;

/// <summary>
/// Measures fix summary rendering with optional per-rule breakdown table.
/// </summary>
[MemoryDiagnoser]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class FixSummaryOutputBenchmark
{
    private List<(string FilePath, int FixedCount)> _fixedFiles = [];
    private List<Diagnostic> _remainingDiagnostics = [];
    private Dictionary<string, int> _fixedByRule = [];

    [GlobalSetup]
    public void Setup()
    {
        _fixedFiles =
        [
            (".github/workflows/ci.yml", 4),
            (".github/workflows/release.yml", 3),
            (".github/workflows/nightly.yml", 2),
        ];
        _remainingDiagnostics =
        [
            new(DiagnosticSeverity.Warning, "msg", new TextRange(0, 1, 1, 1, 1, 2), RuleId: "unpinned-uses", FilePath: ".github/workflows/release.yml"),
        ];
        _fixedByRule = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["if-expr-wrapper"] = 4,
            ["job-timeout-minutes-required"] = 3,
            ["job-permissions-required"] = 2,
        };
    }

    [Benchmark(Baseline = true, Description = "WriteFixSummary per-file only")]
    public int WriteFixSummaryPerFileOnly()
    {
        var sb = new StringBuilder(capacity: 512);
        using var writer = new StringWriter(sb);
        FixCommand.WriteFixSummary(writer, _fixedFiles, _remainingDiagnostics, FixCommand.FixSummaryMode.DryRun);
        return sb.Length;
    }

    [Benchmark(Description = "WriteFixSummary with per-rule table")]
    public int WriteFixSummaryWithPerRuleTable()
    {
        var sb = new StringBuilder(capacity: 768);
        using var writer = new StringWriter(sb);
        FixCommand.WriteFixSummary(
            writer,
            _fixedFiles,
            _remainingDiagnostics,
            FixCommand.FixSummaryMode.DryRun,
            verbose: true,
            fixedByRule: _fixedByRule);
        return sb.Length;
    }
}
