using Seiton.Core.Linting.OnlineAudit;

namespace Seiton.Core.Linting.Rules;

public sealed class KnownVulnerableActionsRule : OnlineRuleBase
{
    public const string RuleId = "known-vulnerable-actions";

    public override string Id => RuleId;

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
