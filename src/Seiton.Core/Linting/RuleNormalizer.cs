using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>
/// Shared rule configuration normalization for lint runtime (<see cref="LintEngine"/>) and config validation (<see cref="LintConfigLibrary"/>).
/// </summary>
internal static class RuleNormalizer
{
    /// <summary>Builds an error message for an unknown rule ID, with a suggestion if a close match exists.</summary>
    public static string BuildUnknownRuleIdMessage(string unknownRuleId)
    {
        var suggested = RuleCatalog.SuggestRuleId(unknownRuleId);
        return suggested is null
            ? $"unknown rule-id '{unknownRuleId}'"
            : $"unknown rule-id '{unknownRuleId}'. Did you mean '{suggested}'?";
    }

    /// <summary>
    /// Resolves rule IDs and runs <see cref="RuleConfigNormalizer"/>.
    /// </summary>
    public static void NormalizeRuleEntries(
        IReadOnlyDictionary<string, RuleConfig> rules,
        string filePath,
        List<Diagnostic> diagnostics,
        Dictionary<string, RuleConfig> destination)
    {
        foreach (var pair in rules)
        {
            if (!RuleCatalog.TryResolveRuleId(pair.Key, out var resolvedRuleId))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    BuildUnknownRuleIdMessage(pair.Key),
                    new TextRange(0, pair.Key.Length, 1, 1, 1, 1 + pair.Key.Length),
                    FilePath: filePath));
                continue;
            }

            var config = pair.Value;
            var resolvedRuleIdString = resolvedRuleId.ToId();

            config = RuleConfigNormalizer.Normalize(config, filePath, diagnostics);
            destination[resolvedRuleIdString] = config;
        }
    }
}
