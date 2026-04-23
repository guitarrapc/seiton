using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class DenyInheritSecretsRule() : RuleBase(RuleId.DenyInheritSecrets)
{
    public override string Name => "Deny Inherit Secrets Rule";

    public override void VisitJobPre(Job job)
    {
        var workflowCall = job.WorkflowCall;
        if (workflowCall is null || !workflowCall.InheritSecrets || !HasNodeValue(workflowCall.Uses, Arena))
        {
            return;
        }

        var jobId = Decode(Arena.GetStringSlice(job.Id));
        AddJobError(
            job,
            $"job '{jobId}' uses 'secrets: inherit' when calling reusable workflow; explicitly map only required secrets via 'secrets:'",
            BuildJobLocation(job));
    }
}
