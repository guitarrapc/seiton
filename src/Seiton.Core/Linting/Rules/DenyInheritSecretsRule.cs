using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags reusable workflow calls using <c>secrets: inherit</c> which exposes all caller secrets.</summary>
public sealed class DenyInheritSecretsRule() : RuleBase(RuleId.DenyInheritSecrets)
{
    public override string Name => "Deny Inherit Secrets Rule";

    public override void VisitJobPre(JobRef job)
    {
        var workflowCall = job.WorkflowCall;
        if (!workflowCall.HasValue || !workflowCall.InheritSecrets || !workflowCall.Uses.HasText)
        {
            return;
        }

        var jobId = job.Id.Decode();
        AddJobError(
            job,
            $"jobs.'{jobId}' uses 'secrets: inherit' when calling reusable workflow; explicitly map only required secrets via 'secrets:'",
            BuildJobLocation(job));
    }
}
