using Seiton.Core.Linting.OnlineAudit;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.Rules;

public sealed class KnownVulnerableActionsRule
{
    public const string RuleId = "known-vulnerable-actions";

    public Diagnostic? Evaluate(ActionAuditTarget target, ActionAdvisory? advisory)
    {
        if (advisory is null)
        {
            return null;
        }

        return new Diagnostic(
            DiagnosticSeverity.Error,
            $"action uses '{target.UsesText}' matches vulnerable advisory '{advisory.AdvisoryId}': {advisory.Summary}",
            target.Location,
            RuleId,
            FilePath: target.FilePath);
    }
}
