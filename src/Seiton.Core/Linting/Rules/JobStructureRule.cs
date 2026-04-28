using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates cross-key structural constraints on job definitions (e.g. <c>steps</c> vs <c>uses</c> mutual exclusion).</summary>
public sealed class JobStructureRule() : RuleBase(RuleId.JobStructure)
{
    public override string Name => "Job Structure Rule";

    public override void VisitJobPre(Job job)
    {
        // Use key presence (not value) to detect reusable workflow calls.
        // An empty `uses:` key still indicates a reusable workflow call intent;
        // the parser reports the empty-value error separately.
        var hasUsesKey = job.WorkflowCall?.UsesKeyRange is not null;
        var hasUsesValue = HasNodeValue(job.WorkflowCall?.Uses ?? default, Arena);
        var hasRunsOn = job.RunsOn is not null;
        var hasSteps = job.Steps is not null;
        var jobId = Decode(Arena.GetStringSlice(job.Id));

        if (hasUsesValue && hasSteps)
        {
            AddJobError(job, $"jobs.'{jobId}' cannot have both uses and steps");
        }

        if (hasUsesValue && hasRunsOn)
        {
            AddJobError(job, $"jobs.'{jobId}' cannot have both uses and runs-on");
        }

        if (!hasUsesKey && !hasRunsOn)
        {
            AddJobError(job, $"\"runs-on\" section is missing in jobs.'{jobId}'");
        }

        if (!hasUsesKey && !hasSteps)
        {
            AddJobError(job, $"\"steps\" section is missing in jobs.'{jobId}'");
        }
    }
}
