using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public sealed class ReusableWorkflowSecretsInheritRule : RuleBase
{
    public override string Id => "reusable-workflow-secrets-inherit";

    public override string Name => "Reusable Workflow Secrets Inherit Rule";

    public override void VisitJobPre(Job job)
    {
        var workflowCall = job.WorkflowCall;
        if (workflowCall is null || !workflowCall.InheritSecrets || !HasNodeValue(workflowCall.Uses))
        {
            return;
        }

        var jobId = Decode(job.Id.Value);
        AddJobWarning(
            job,
            $"job '{jobId}' uses 'secrets: inherit' when calling reusable workflow; explicitly map only required secrets via 'secrets:'");
    }
}