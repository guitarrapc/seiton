using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>
/// Shared rule configuration normalization for lint runtime (<see cref="LintEngine"/>) and config validation (<see cref="LintConfigLibrary"/>).
/// </summary>
internal static class RuleNormalizer
{
    public static string BuildUnknownRuleIdMessage(string unknownRuleId)
    {
        var suggested = RuleCatalog.SuggestRuleId(unknownRuleId);
        return suggested is null
            ? $"unknown rule-id '{unknownRuleId}'"
            : $"unknown rule-id '{unknownRuleId}'. Did you mean '{suggested}'?";
    }

    /// <summary>
    /// Resolves rule IDs, enforces non-disableable and minimum-severity policy, and runs <see cref="RuleConfigNormalizer"/>.
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
            if (!config.Enabled && RuleCatalog.IsNonDisableable(resolvedRuleId))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"rule '{resolvedRuleId}' is non-disableable",
                    new TextRange(0, pair.Key.Length, 1, 1, 1, 1 + pair.Key.Length),
                    FilePath: filePath));
                config = config with { Enabled = true };
            }

            if (config.Severity is not null
                && RuleCatalog.TryGetMinimumSeverity(resolvedRuleId, out var minimumSeverity)
                && config.Severity.Value < minimumSeverity)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"rule '{resolvedRuleId}' minimum severity is '{minimumSeverity}', but '{config.Severity.Value}' was specified",
                    new TextRange(0, pair.Key.Length, 1, 1, 1, 1 + pair.Key.Length),
                    FilePath: filePath));
                config = config with { Severity = null };
            }

            config = RuleConfigNormalizer.Normalize(config, resolvedRuleId, filePath, diagnostics);
            destination[resolvedRuleId] = config;
        }
    }
}
