using Seiton.Core.Linting.OnlineAudit;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.Rules;

public sealed class StaleActionRefsRule
{
    public const string RuleId = "stale-action-refs";

    public Diagnostic? Evaluate(ActionAuditTarget target, ActionRefResolution resolution)
    {
        if (!target.IsCommitSha || !resolution.CommitExists || resolution.IsTaggedCommit)
        {
            return null;
        }

        return new Diagnostic(
            DiagnosticSeverity.Warning,
            $"action uses '{target.UsesText}' pins commit '{target.Reference}' that is not associated with any current tag in '{target.Owner}/{target.Repo}'",
            target.Location,
            RuleId,
            FilePath: target.FilePath);
    }
}
