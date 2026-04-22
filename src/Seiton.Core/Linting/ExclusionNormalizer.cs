using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>
/// Shared exclusion rule-id resolution for <see cref="LintEngine"/> and <see cref="LintConfigLibrary"/>.
/// Job-id validation and file-pattern shaping stay at each call site.
/// </summary>
internal static class ExclusionNormalizer
{
    public static void CollectResolvedExclusionRules(
        IReadOnlyList<string> ruleIds,
        string filePath,
        List<Diagnostic> diagnostics,
        HashSet<string> normalizedRuleIds)
    {
        for (var j = 0; j < ruleIds.Count; j++)
        {
            var ruleId = ruleIds[j];
            if (RuleCatalog.TryResolveRuleId(ruleId, out var resolvedRuleId))
            {
                if (RuleCatalog.IsNonDisableable(resolvedRuleId))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        $"rule '{resolvedRuleId}' is non-disableable",
                        new TextRange(0, ruleId.Length, 1, 1, 1, 1 + ruleId.Length),
                        FilePath: filePath));
                    continue;
                }

                normalizedRuleIds.Add(resolvedRuleId);
                continue;
            }

            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                RuleNormalizer.BuildUnknownRuleIdMessage(ruleId),
                new TextRange(0, ruleId.Length, 1, 1, 1, 1 + ruleId.Length),
                FilePath: filePath));
        }
    }
}
