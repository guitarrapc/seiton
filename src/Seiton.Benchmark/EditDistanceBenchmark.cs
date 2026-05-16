using Seiton.Core.Linting;

namespace Seiton.Benchmark;

[MemoryDiagnoser]
[RankColumn]
public class EditDistanceBenchmark
{
    private string[] _lefts = null!;
    private string[] _rights = null!;

    // Simulates PopularActionInputsRule: unknown input vs 55 candidates (actions/stale)
    private string[] _staleCandidates = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Simulate typical usage: comparing unknown input names against known inputs
        _lefts = ["tokne", "scrpt", "environment-url", "node-version", "registryUrl", "cache-dependency-pathx"];
        _rights = ["token", "script", "environment-url", "node-version", "registry-url", "cache-dependency-path"];

        // Largest real candidate set: actions/stale has 55 inputs
        _staleCandidates =
        [
            "repo-token", "stale-issue-message", "stale-pr-message", "close-issue-message",
            "close-pr-message", "days-before-stale", "days-before-close", "days-before-issue-stale",
            "days-before-issue-close", "days-before-pr-stale", "days-before-pr-close",
            "stale-issue-label", "close-issue-label", "exempt-issue-labels", "stale-pr-label",
            "close-pr-label", "exempt-pr-labels", "exempt-milestones", "exempt-issue-milestones",
            "exempt-pr-milestones", "exempt-all-milestones", "only-labels", "only-issue-labels",
            "only-pr-labels", "any-of-labels", "any-of-issue-labels", "any-of-pr-labels",
            "operations-per-run", "remove-stale-when-updated", "remove-issue-stale-when-updated",
            "remove-pr-stale-when-updated", "debug-only", "ascending", "start-date",
            "delete-branch", "exempt-assignees", "exempt-issue-assignees", "exempt-pr-assignees",
            "exempt-all-assignees", "exempt-draft-pr", "enable-statistics", "labels-to-add-when-unstale",
            "labels-to-remove-when-unstale", "ignore-updates", "ignore-issue-updates",
            "ignore-pr-updates", "include-only-assigned", "exempt-issue-close-reason",
            "close-issue-reason", "stale-issue-close-reason", "exempt-pr-close-reason",
            "close-pr-reason", "stale-pr-close-reason", "exempt-all-close-reason",
            "close-all-reason"
        ];
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

    [Benchmark]
    public int WithMaxDistance_EarlyReject()
    {
        // Length difference 16 > maxDistance 3 → immediate return
        return EditDistance.ComputeIgnoreCase("cache-dependency-pathx", "token", maxDistance: 3);
    }

    [Benchmark]
    public int WithMaxDistance_WithinThreshold()
    {
        // Distance is 1, maxDistance is 3 → computes actual distance
        return EditDistance.ComputeIgnoreCase("node_version", "node-version", maxDistance: 3);
    }

    [Benchmark]
    public int FindClosest_55Candidates()
    {
        // Simulate PopularActionInputsRule: find closest among 55 candidates with maxDistance cutoff
        var input = "days-before-stall";
        var maxDistance = Math.Max(2, input.Length / 3); // = 5
        var best = int.MaxValue;

        for (var i = 0; i < _staleCandidates.Length; i++)
        {
            var d = EditDistance.ComputeIgnoreCase(input, _staleCandidates[i], maxDistance);
            if (d < best)
            {
                best = d;
            }
        }

        return best;
    }

    [Benchmark]
    public int FindClosest_55Candidates_NoMatch()
    {
        // Simulate case where no candidate matches (all distances > threshold)
        var input = "zzzzzzzzz";
        var maxDistance = Math.Max(2, input.Length / 3); // = 3
        var best = int.MaxValue;

        for (var i = 0; i < _staleCandidates.Length; i++)
        {
            var d = EditDistance.ComputeIgnoreCase(input, _staleCandidates[i], maxDistance);
            if (d < best)
            {
                best = d;
            }
        }

        return best;
    }
}
