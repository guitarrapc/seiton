using Seiton.Core.Linting.OnlineAudit;

namespace Seiton.Core.Linting.Rules;

public sealed class ImpostorCommitRule() : OnlineRuleBase(RuleId.ImpostorCommit)
{
    public override string Name => "Impostor Commit";

    public override void EvaluateTarget(ActionAuditTarget target, ActionAdvisory? advisory, ActionRefResolution? resolution)
    {
        if (resolution is null || !target.IsCommitSha || resolution.Value.CommitExists)
        {
            return;
        }

        AddError(
            $"action uses '{target.UsesText}' pins commit '{target.Reference}' that is not reachable in '{target.Owner}/{target.Repo}'",
            target.Location);
    }
}
