using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates <c>needs:</c> job dependency graphs for undefined references and cycles.</summary>
public sealed class NeedsGraphRule() : RuleBase(RuleId.NeedsGraph)
{
    public override string Name => "Needs Graph Rule";

    private SliceMap<Job> _knownJobs;

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        _knownJobs = workflow.Jobs;
    }

    public override void VisitJobPre(Job job)
    {
        if (job.Needs is null || Config.Utf8Yaml is null)
        {
            return;
        }

        for (var i = 0; i < job.Needs.Length; i++)
        {
            var need = job.Needs[i];
            var needSpan = Arena.GetStringValue(need);
            if (!_knownJobs.ContainsKey(Config.Utf8Yaml, needSpan))
            {
                var jobId = Decode(Arena.GetStringSlice(job.Id));
                var needText = Decode(Arena.GetStringSlice(need));
                AddJobError(job, $"jobs.'{jobId}'.needs references unknown job '{needText}'", Arena.GetStringRange(need));
            }

            // Check for duplicates among earlier entries (case-insensitive, GitHub Actions job IDs are case-insensitive)
            for (var j = 0; j < i; j++)
            {
                var earlier = job.Needs[j];
                if (EqualsAsciiIgnoreCase(needSpan, Arena.GetStringValue(earlier)))
                {
                    var jobId = Decode(Arena.GetStringSlice(job.Id));
                    var needText = Decode(Arena.GetStringSlice(need));
                    AddJobError(job, $"jobs.'{jobId}'.needs has duplicates '{needText}'", Arena.GetStringRange(need));
                    break;
                }
            }
        }
    }

    public override void VisitWorkflowPost(Workflow workflow)
    {
        DetectCycles();
    }

    private void DetectCycles()
    {
        if (_knownJobs.Count == 0 || Config.Utf8Yaml is null)
        {
            return;
        }

        var source = Config.Utf8Yaml;

        // DFS cycle detection using colors: 0=unvisited, 1=in-progress (gray), 2=done (black)
        var color = new Dictionary<Utf8String, byte>(_knownJobs.Count);
        foreach (var pair in _knownJobs)
        {
            color[pair.Key.ToUtf8StringZeroCopy(source)] = 0;
        }

        var stack = new Stack<(Utf8String Key, int NeighborIndex)>();

        foreach (var kvp in _knownJobs)
        {
            var key = kvp.Key.ToUtf8StringZeroCopy(source);
            if (color[key] != 0)
            {
                continue;
            }

            color[key] = 1;
            stack.Push((key, 0));

            while (stack.Count > 0)
            {
                var (currentKey, ni) = stack.Peek();
                if (!_knownJobs.TryGetValue(source, currentKey.Span, out var currentJob))
                {
                    stack.Pop();
                    color[currentKey] = 2;
                    continue;
                }

                var needs = currentJob.Needs;

                if (needs is null || ni >= needs.Length)
                {
                    stack.Pop();
                    color[currentKey] = 2;
                    continue;
                }

                // Advance the neighbor index for the current node before descending
                stack.Pop();
                stack.Push((currentKey, ni + 1));

                var need = needs[ni];
                var needSpan = Arena.GetStringValue(need);
                var needKey = Utf8String.FromLowerAscii(needSpan);

                if (!color.TryGetValue(needKey, out var neighborColor))
                {
                    continue; // unknown job reference — already reported in VisitJobPre
                }

                if (neighborColor == 1) // gray: back-edge = cycle
                {
                    // Build cycle path from DFS stack for informative message
                    var cyclePath = BuildCyclePath(source, stack, needKey);
                    // Report at the first job in the cycle
                    var cycleStartJob = FindCycleStartJob(source, stack, needKey);
                    var reportJob = cycleStartJob ?? currentJob;
                    var reportRange = cycleStartJob is not null
                        ? Arena.GetStringRange(cycleStartJob.Id)
                        : Arena.GetStringRange(need);
                    AddJobError(reportJob, $"cyclic dependencies in \"needs\" job configurations are detected. detected cycle is {cyclePath}", reportRange);
                }
                else if (neighborColor == 0)
                {
                    color[needKey] = 1;
                    stack.Push((needKey, 0));
                }
            }
        }
    }

    /// <summary>
    /// Builds a human-readable cycle path string from the DFS stack.
    /// Example: "a -> b -> c -> a"
    /// </summary>
    private string BuildCyclePath(byte[] source, Stack<(Utf8String Key, int NeighborIndex)> stack, Utf8String cycleTarget)
    {
        // Stack contains the gray path from root to current node.
        // Find cycleTarget in the stack to extract only the cycle portion.
        var stackArray = stack.ToArray(); // top-of-stack first
        Array.Reverse(stackArray); // now root-first order

        var sb = new System.Text.StringBuilder();
        var inCycle = false;
        foreach (var (key, _) in stackArray)
        {
            if (!inCycle && key.Equals(cycleTarget))
            {
                inCycle = true;
            }

            if (inCycle)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" -> ");
                }

                if (_knownJobs.TryGetValue(source, key.Span, out var job))
                {
                    sb.Append('"');
                    sb.Append(Decode(Arena.GetStringSlice(job.Id)));
                    sb.Append('"');
                }
            }
        }

        // Close the cycle by appending the target again
        if (sb.Length > 0)
        {
            sb.Append(" -> ");
            if (_knownJobs.TryGetValue(source, cycleTarget.Span, out var targetJob))
            {
                sb.Append('"');
                sb.Append(Decode(Arena.GetStringSlice(targetJob.Id)));
                sb.Append('"');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Finds the first job in the cycle (the cycle target) from the DFS stack.
    /// Returns null if not found.
    /// </summary>
    private Job? FindCycleStartJob(byte[] source, Stack<(Utf8String Key, int NeighborIndex)> stack, Utf8String cycleTarget)
    {
        return _knownJobs.TryGetValue(source, cycleTarget.Span, out var job) ? job : null;
    }

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a == b)
            {
                continue;
            }

            if (a is >= (byte)'A' and <= (byte)'Z')
            {
                a = (byte)(a + 32);
            }

            if (b is >= (byte)'A' and <= (byte)'Z')
            {
                b = (byte)(b + 32);
            }

            if (a != b)
            {
                return false;
            }
        }

        return true;
    }
}
