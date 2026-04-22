using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class JobStructureRule : RuleBase
{
    public override string Id => "job-structure";

    public override string Name => "Job Structure Rule";

    public override void VisitJobPre(Job job)
    {
        var hasUses = HasNodeValue(job.WorkflowCall?.Uses ?? default, Arena);
        var hasRunsOn = job.RunsOn is not null;
        var hasSteps = job.Steps is not null;
        var jobId = Decode(Arena.GetStringSlice(job.Id));

        if (hasUses && hasSteps)
        {
            AddJobError(job, $"job '{jobId}' cannot have both uses and steps");
        }

        if (hasUses && hasRunsOn)
        {
            AddJobError(job, $"job '{jobId}' cannot have both uses and runs-on");
        }

        if (!hasUses && !hasRunsOn)
        {
            AddJobError(job, $"job '{jobId}' requires runs-on (or uses)");
        }

        if (!hasUses && !hasSteps)
        {
            AddJobError(job, $"job '{jobId}' requires steps (or uses)");
        }
    }
}
