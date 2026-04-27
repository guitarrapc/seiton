using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates cross-key structural constraints on job definitions (e.g. <c>steps</c> vs <c>uses</c> mutual exclusion).</summary>
public sealed class JobStructureRule() : RuleBase(RuleId.JobStructure)
{
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
            AddJobError(job, $"\"runs-on\" section is missing in job \"{jobId}\"");
        }

        if (!hasUses && !hasSteps)
        {
            AddJobError(job, $"\"steps\" section is missing in job \"{jobId}\"");
        }
    }
}
