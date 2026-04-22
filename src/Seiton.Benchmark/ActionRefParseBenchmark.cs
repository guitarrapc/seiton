using System.Text;
using Seiton.Core.Linting;

namespace Seiton.Benchmark;

/// <summary>
/// Micro-benchmarks for unified <see cref="ActionRefHelpers"/> paths used by forbidden-uses,
/// unpinned-uses, and ref-version-mismatch rules (UTF-8 spans, no workflow parse).
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class ActionRefParseBenchmark
{
    private byte[] _simple = [];
    private byte[] _subPath = [];
    private const string SimpleUses = "actions/checkout@v4";
    private const string SubPathUses = "acme/widgets/.github/actions/build/action.yml@v2";

    [GlobalSetup]
    public void Setup()
    {
        _simple = Encoding.UTF8.GetBytes(SimpleUses);
        _subPath = Encoding.UTF8.GetBytes(SubPathUses);
    }

    [Benchmark(Baseline = true, Description = "TryParseRemoteUses (short uses)")]
    public bool ParseRemoteUses_Simple()
    {
        return ActionRefHelpers.TryParseRemoteUses(_simple, out _);
    }

    [Benchmark(Description = "TryParseRemoteUses (uses with subpath + .yml)")]
    public bool ParseRemoteUses_SubPath()
    {
        return ActionRefHelpers.TryParseRemoteUses(_subPath, out _);
    }

    [Benchmark(Description = "Parse + TryGetOwnerRepoPolicyKey (forbidden-uses, stack scratch)")]
    public bool ForbiddenUses_KeyFromPath()
    {
        if (!ActionRefHelpers.TryParseRemoteUses(_simple, out var parsed))
        {
            return false;
        }

        Span<byte> scratch = stackalloc byte[512];
        return ActionRefHelpers.TryGetOwnerRepoPolicyKey(parsed.ActionPath, scratch, out _);
    }

    [Benchmark(Description = "Parse + ref/path major (ref-version-mismatch)")]
    public bool RefVersionMismatch_Majors()
    {
        if (!ActionRefHelpers.TryParseRemoteUses(_subPath, out var parsed))
        {
            return false;
        }

        return ActionRefHelpers.TryExtractRefVersionMajor(parsed.Ref, out _)
               && ActionRefHelpers.TryExtractPathVersionMajor(parsed.ActionPath, out _);
    }

    [Benchmark(Description = "TryParseActionReference(string) stackalloc path")]
    public bool ParseActionReference_String()
    {
        return ActionRefHelpers.TryParseActionReference(SimpleUses, out _, out _, out _);
    }
}
