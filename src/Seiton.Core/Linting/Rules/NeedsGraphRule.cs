using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class NeedsGraphRule : RuleBase
{
    public override string Id => "needs-graph";

    public override string Name => "Needs Graph Rule";

    private IReadOnlyDictionary<Utf8String, Job> _knownJobs = new Dictionary<Utf8String, Job>();

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

        for (var i = 0; i < job.Needs.Count; i++)
        {
            var need = job.Needs[i];
            var needSpan = need.Value.AsSpan(Config.Utf8Yaml);
            var needKey = Utf8String.FromLowerAscii(needSpan);
            if (!_knownJobs.ContainsKey(needKey))
            {
                var jobId = Decode(job.Id.Value);
                var needText = Decode(need.Value);
                AddJobError(job, $"job '{jobId}' references unknown job '{needText}' in needs", need.Range);
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

        // DFS cycle detection using colors: 0=unvisited, 1=in-progress (gray), 2=done (black)
        var color = new Dictionary<Utf8String, byte>(_knownJobs.Count);
        foreach (var key in _knownJobs.Keys)
        {
            color[key] = 0;
        }

        var stack = new Stack<(Utf8String Key, int NeighborIndex)>();

        foreach (var kvp in _knownJobs)
        {
            if (color[kvp.Key] != 0)
            {
                continue;
            }

            color[kvp.Key] = 1;
            stack.Push((kvp.Key, 0));

            while (stack.Count > 0)
            {
                var (currentKey, ni) = stack.Peek();
                var currentJob = _knownJobs[currentKey];
                var needs = currentJob.Needs;

                if (needs is null || ni >= needs.Count)
                {
                    stack.Pop();
                    color[currentKey] = 2;
                    continue;
                }

                // Advance the neighbor index for the current node before descending
                stack.Pop();
                stack.Push((currentKey, ni + 1));

                var need = needs[ni];
                var needSpan = need.Value.AsSpan(Config.Utf8Yaml);
                var needKey = Utf8String.FromLowerAscii(needSpan);

                if (!color.TryGetValue(needKey, out var neighborColor))
                {
                    continue; // unknown job reference ? already reported in VisitJobPre
                }

                if (neighborColor == 1) // gray: back-edge = cycle
                {
                    var jobId = Decode(currentJob.Id.Value);
                    var needText = Decode(need.Value);
                    AddJobError(currentJob, $"job '{jobId}' has a circular 'needs' dependency via '{needText}'", need.Range);
                }
                else if (neighborColor == 0)
                {
                    color[needKey] = 1;
                    stack.Push((needKey, 0));
                }
            }
        }
    }
}
