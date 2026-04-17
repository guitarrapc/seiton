using Seiton.Core.Linting.OnlineAudit;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.Rules;

public sealed class ImpostorCommitRule
{
    public const string RuleId = "impostor-commit";

    public Diagnostic? Evaluate(ActionAuditTarget target, ActionRefResolution resolution)
    {
        if (!target.IsCommitSha || resolution.CommitExists)
        {
            return null;
        }

        return new Diagnostic(
            DiagnosticSeverity.Error,
            $"action uses '{target.UsesText}' pins commit '{target.Reference}' that is not reachable in '{target.Owner}/{target.Repo}'",
            target.Location,
            RuleId,
            FilePath: target.FilePath);
    }
}
