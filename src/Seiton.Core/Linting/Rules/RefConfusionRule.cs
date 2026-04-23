using Seiton.Core.Linting.OnlineAudit;

namespace Seiton.Core.Linting.Rules;

public sealed class RefConfusionRule : OnlineRuleBase
{
    public const string RuleId = "ref-confusion";

    public override string Id => RuleId;

    public override string Name => "Ref Confusion";

    public override void EvaluateTarget(ActionAuditTarget target, ActionAdvisory? advisory, ActionRefResolution? resolution)
    {
        if (resolution is null || target.IsCommitSha || !resolution.Value.HasBranchReference || !resolution.Value.HasTagReference)
        {
            return;
        }

        AddError(
            $"action uses '{target.UsesText}' references ambiguous symbolic ref '{target.Reference}' present as both branch and tag in '{target.Owner}/{target.Repo}'",
            target.Location);
    }
}
