using Seiton.Core.Linting.OnlineAudit;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.Rules;

public sealed class RefConfusionRule
{
    public const string RuleId = "ref-confusion";

    public Diagnostic? Evaluate(ActionAuditTarget target, ActionRefResolution resolution)
    {
        if (target.IsCommitSha || !resolution.HasBranchReference || !resolution.HasTagReference)
        {
            return null;
        }

        return new Diagnostic(
            DiagnosticSeverity.Error,
            $"action uses '{target.UsesText}' references ambiguous symbolic ref '{target.Reference}' present as both branch and tag in '{target.Owner}/{target.Repo}'",
            target.Location,
            RuleId,
            FilePath: target.FilePath);
    }
}
