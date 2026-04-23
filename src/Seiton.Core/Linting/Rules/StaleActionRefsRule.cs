using Seiton.Core.Linting.OnlineAudit;

namespace Seiton.Core.Linting.Rules;

public sealed class StaleActionRefsRule() : OnlineRuleBase(RuleId.StaleActionRefs)
{
    public override string Name => "Stale Action Refs";

    public override void EvaluateTarget(ActionAuditTarget target, ActionAdvisory? advisory, ActionRefResolution? resolution)
    {
        if (resolution is null || !target.IsCommitSha || !resolution.Value.CommitExists || resolution.Value.IsTaggedCommit)
        {
            return;
        }

        AddWarning(
            $"action uses '{target.UsesText}' pins commit '{target.Reference}' that is not associated with any current tag in '{target.Owner}/{target.Repo}'",
            target.Location);
    }
}
