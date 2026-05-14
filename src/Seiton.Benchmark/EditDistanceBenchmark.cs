using Seiton.Core.Linting;

namespace Seiton.Benchmark;

[MemoryDiagnoser]
[RankColumn]
public class EditDistanceBenchmark
{
    private string[] _lefts = null!;
    private string[] _rights = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Simulate typical usage: comparing unknown input names against known inputs
        _lefts = ["tokne", "scrpt", "environment-url", "node-version", "registryUrl", "cache-dependency-pathx"];
        _rights = ["token", "script", "environment-url", "node-version", "registry-url", "cache-dependency-path"];
    }

    [Benchmark]
    public int ComputeAll()
    {
        var sum = 0;
        for (var i = 0; i < _lefts.Length; i++)
        {
            for (var j = 0; j < _rights.Length; j++)
            {
                sum += EditDistance.ComputeIgnoreCase(_lefts[i], _rights[j]);
            }
        }

        return sum;
    }

    [Benchmark]
    public int SingleShort()
    {
        return EditDistance.ComputeIgnoreCase("tokne", "token");
    }

    [Benchmark]
    public int SingleLong()
    {
        return EditDistance.ComputeIgnoreCase("cache-dependency-pathx", "cache-dependency-path");
    }
}
