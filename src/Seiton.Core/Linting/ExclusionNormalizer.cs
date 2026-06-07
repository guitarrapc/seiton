using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>
/// Shared exclusion rule-id resolution for <see cref="LintEngine"/> and <see cref="LintConfigLibrary"/>.
/// Job-id validation and file-pattern shaping stay at each call site.
/// </summary>
internal static class ExclusionNormalizer
{
    /// <summary>Wildcard rule-id that suppresses all rules in an exclusion entry.</summary>
    public const string AllRulesWildcard = "*";

    /// <summary>Returns <c>true</c> when <paramref name="ruleIds"/> contains the all-rules wildcard.</summary>
    public static bool IsAllRulesWildcard(IReadOnlyList<string> ruleIds)
    {
        for (var j = 0; j < ruleIds.Count; j++)
        {
            if (ruleIds[j] == AllRulesWildcard)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves and validates each rule ID in <paramref name="ruleIds"/>, adding normalized IDs to <paramref name="normalizedRuleIds"/> and emitting diagnostics for unknown rules.
    /// </summary>
    public static void CollectResolvedExclusionRules(
        IReadOnlyList<string> ruleIds,
        string filePath,
        List<Diagnostic> diagnostics,
        HashSet<string> normalizedRuleIds)
    {
        for (var j = 0; j < ruleIds.Count; j++)
        {
            var ruleId = ruleIds[j];
            if (ruleId == AllRulesWildcard)
            {
                continue;
            }

            if (RuleCatalog.TryResolveRuleId(ruleId, out var resolvedRuleId))
            {
                normalizedRuleIds.Add(resolvedRuleId.ToId());
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
