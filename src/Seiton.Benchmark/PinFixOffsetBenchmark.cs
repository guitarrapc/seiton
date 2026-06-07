using System.Text;
using BenchmarkDotNet.Attributes;
using Seiton.Core.Linting;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;

namespace Seiton.Benchmark;

/// <summary>
/// Measures pin fix offset resolution for workflows with repeated identical action references.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class PinFixOffsetBenchmark
{
    private byte[] _source = [];
    private Diagnostic[] _diagnostics = [];

    [Params(2, 8)]
    public int DuplicateUsesCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder();
        sb.AppendLine("on: push");
        sb.AppendLine("jobs:");
        for (var i = 0; i < DuplicateUsesCount; i++)
        {
            sb.Append("  job").Append(i).AppendLine(":");
            sb.AppendLine("    steps:");
            sb.AppendLine("      - uses: actions/github-script@v9");
        }

        _source = Encoding.UTF8.GetBytes(sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
        var engine = new LintEngine([new UnpinnedUsesRule()]);
        using var result = engine.Check(_source, "pin-fix-offset-bench.yml");
        _diagnostics = result.Diagnostics
            .Where(d => d.RuleId == "unpinned-uses")
            .ToArray();
    }

    [Benchmark(Description = "Resolve pin fix offsets for duplicate uses")]
    public int ResolvePinFixOffsets()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var distinctOffsets = 0;
        var lastOffset = -1;
        for (var i = 0; i < _diagnostics.Length; i++)
        {
            var fix = PinFixFormatter.BuildActionsShaFix(_diagnostics[i], sha, "v9", _source);
            if (!fix.HasValue)
            {
                continue;
            }

            var offset = fix.Value.Edits[0].Offset;
            if (offset != lastOffset)
            {
                distinctOffsets++;
                lastOffset = offset;
            }
        }

        return distinctOffsets;
    }
}
