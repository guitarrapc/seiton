using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class JobTimeoutMinutesRequiredRule : RuleBase
{
    public override string Id => "job-timeout-minutes-required";

    public override string Name => "Job Timeout Minutes Required Rule";

    public override void VisitJobPre(Job job)
    {
        if (!IsExecutableJob(job) || job.TimeoutMinutes is not null)
        {
            return;
        }

        if (AllStepsHaveTimeout(job.Steps))
        {
            return;
        }

        var jobId = Decode(job.Id.Value);
        AddJobError(
            job,
            $"job '{jobId}' must define timeout-minutes; alternatively, set timeout-minutes on every step",
            BuildJobLocation(job));
    }

    static bool IsExecutableJob(Job job)
    {
        return job.WorkflowCall is null
            && job.Steps is not null
            && job.Steps.Count > 0;
    }

    static bool AllStepsHaveTimeout(IReadOnlyList<Step>? steps)
    {
        if (steps is null || steps.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].TimeoutMinutes is null)
            {
                return false;
            }
        }

        return true;
    }
}
