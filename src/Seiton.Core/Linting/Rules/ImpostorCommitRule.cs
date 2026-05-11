using Seiton.Core.Linting.OnlineAudit;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags action references pinned to commit SHAs that may be impostor commits (not on any branch/tag).</summary>
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
            $"'{target.UsesText}' pins commit '{target.Reference}' that is not reachable in '{target.Owner}/{target.Repo}'",
            target.Location);
    }
}
