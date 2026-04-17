using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class ReusableWorkflowRule : RuleBase
{
    public override string Id => "reusable-workflow";

    public override string Name => "Reusable Workflow Rule";

    public override void VisitJobPre(Job job)
    {
        var workflowCall = job.WorkflowCall;
        if (workflowCall is null)
        {
            return;
        }

        var jobId = Decode(job.Id.Value);
        var hasUses = HasNodeValue(workflowCall.Uses);

        if (!hasUses)
        {
            if (workflowCall.Inputs is not null && workflowCall.Inputs.Count > 0)
            {
                AddJobError(job, $"job '{jobId}' key 'with' requires uses");
            }

            if ((workflowCall.Secrets is not null && workflowCall.Secrets.Count > 0) || workflowCall.InheritSecrets)
            {
                AddJobError(job, $"job '{jobId}' key 'secrets' requires uses");
            }

            return;
        }

        ReportIfPresent(job, job.RunsOn is not null, "runs-on", jobId);
        ReportIfPresent(job, job.Environment is not null, "environment", jobId);
        ReportIfPresent(job, job.Outputs is not null && job.Outputs.Count > 0, "outputs", jobId);
        ReportIfPresent(job, job.Env is not null, "env", jobId);
        ReportIfPresent(job, job.Defaults is not null, "defaults", jobId);
        ReportIfPresent(job, job.Steps is not null && job.Steps.Count > 0, "steps", jobId);
        ReportIfPresent(job, job.TimeoutMinutes is not null, "timeout-minutes", jobId);
        ReportIfPresent(job, job.ContinueOnError is not null, "continue-on-error", jobId);
        ReportIfPresent(job, job.Container is not null, "container", jobId);
        ReportIfPresent(job, job.Services is not null, "services", jobId);
    }

    void ReportIfPresent(Job job, bool present, string keyName, string jobId)
    {
        if (!present)
        {
            return;
        }

        AddJobError(job, $"when job '{jobId}' calls reusable workflow with uses, key '{keyName}' is not allowed");
    }
}
