using Seiton.Core.Linting.OnlineAudit;

namespace Seiton.Core.Linting.Rules;

public sealed class KnownVulnerableActionsRule() : OnlineRuleBase(RuleId.KnownVulnerableActions)
{
    public override string Name => "Known Vulnerable Actions";

    public override void EvaluateTarget(ActionAuditTarget target, ActionAdvisory? advisory, ActionRefResolution? resolution)
    {
        if (advisory is null)
        {
            return;
        }

        AddError(
            $"action uses '{target.UsesText}' matches vulnerable advisory '{advisory.AdvisoryId}': {advisory.Summary}",
            target.Location);
    }
}
