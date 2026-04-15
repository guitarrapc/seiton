using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

internal static class RuleCatalog
{
    const string CanonicalPrefix = "seiton-lint-rule-";

    // Rule responsibilities are intentionally split:
    // - job-structure: cross-key structural constraints on Job shape.
    // - reusable-workflow: uses/with/secrets semantics and forbidden keys in reusable calls.
    // - permissions: scalar/scope value domain validation for permissions.
    // - popular-action-inputs: known-action input-name validation (warning-level).
    static readonly (string Id, int Priority, Func<IRule> Factory)[] DefaultRuleFactories =
    [
        ("job-structure", 0, static () => new JobStructureRule()),
        ("reusable-workflow", 1, static () => new ReusableWorkflowRule()),
        ("permissions", 2, static () => new PermissionsRule()),
        ("popular-action-inputs", 3, static () => new PopularActionInputsRule()),
        ("unpinned-uses", 4, static () => new UnpinnedUsesRule()),
        ("unpinned-image", 5, static () => new UnpinnedImageRule()),
        ("dangerous-triggers", 6, static () => new DangerousTriggersRule()),
        ("job-permissions-required", 7, static () => new JobPermissionsRequiredRule()),
        ("needs-graph", 8, static () => new NeedsGraphRule()),
        ("shell-name", 9, static () => new ShellNameRule()),
        ("runner-label", 10, static () => new RunnerLabelRule()),
        ("id-naming", 11, static () => new IdNamingRule()),
        ("glob-pattern", 12, static () => new GlobPatternRule()),
        ("deny-write-all", 13, static () => new DenyWriteAllRule()),
        ("credentials", 14, static () => new CredentialsRule()),
        ("template-injection", 15, static () => new TemplateInjectionRule()),
        ("expr-undefined-var", 16, static () => new ExprUndefinedVarRule()),
    ];

    static readonly IReadOnlyDictionary<string, string> CanonicalRuleIdToRuleId = BuildCanonicalRuleIdMap();

    static readonly IReadOnlyDictionary<string, string> RuleIdToCanonicalRuleId = BuildReverseCanonicalRuleIdMap();

    static readonly IReadOnlySet<string> NonDisableableRuleIds = BuildNonDisableableRuleIdSet();

    static readonly IReadOnlyDictionary<string, DiagnosticSeverity> MinimumSeverities = BuildMinimumSeverityMap();

    public static IRule[] CreateDefaultRules()
    {
        var rules = new IRule[DefaultRuleFactories.Length];
        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            rules[i] = DefaultRuleFactories[i].Factory();
        }

        return rules;
    }

    public static int GetPriority(string? ruleId)
    {
        if (string.IsNullOrEmpty(ruleId))
        {
            return int.MaxValue;
        }

        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            if (string.Equals(DefaultRuleFactories[i].Id, ruleId, StringComparison.Ordinal))
            {
                return DefaultRuleFactories[i].Priority;
            }
        }

        return int.MaxValue - 1;
    }

    public static bool TryResolveRuleId(string? idOrCanonical, out string resolvedRuleId)
    {
        resolvedRuleId = string.Empty;
        if (string.IsNullOrWhiteSpace(idOrCanonical))
        {
            return false;
        }

        if (TryFindRuleIdBySemanticId(idOrCanonical, out resolvedRuleId))
        {
            return true;
        }

        if (!CanonicalRuleIdToRuleId.TryGetValue(idOrCanonical, out var mappedRuleId) || mappedRuleId is null)
        {
            return false;
        }

        resolvedRuleId = mappedRuleId;
        return true;
    }

    public static string? GetCanonicalRuleId(string? ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return null;
        }

        if (RuleIdToCanonicalRuleId.TryGetValue(ruleId, out var canonical))
        {
            return canonical;
        }

        foreach (var pair in RuleIdToCanonicalRuleId)
        {
            if (string.Equals(pair.Key, ruleId, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    public static string? SuggestRuleId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var bestCandidate = string.Empty;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            var candidate = DefaultRuleFactories[i].Id;
            var distance = ComputeEditDistanceIgnoreCase(input, candidate);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            bestCandidate = candidate;
        }

        return bestDistance <= 4 ? bestCandidate : null;
    }

    public static string[] GetDefaultRuleIds()
    {
        var ids = new string[DefaultRuleFactories.Length];
        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            ids[i] = DefaultRuleFactories[i].Id;
        }

        return ids;
    }

    public static bool IsNonDisableable(string? ruleId)
    {
        return TryResolveRuleId(ruleId, out var resolvedRuleId)
            && NonDisableableRuleIds.Contains(resolvedRuleId);
    }

    public static bool TryGetMinimumSeverity(string? ruleId, out DiagnosticSeverity minimumSeverity)
    {
        minimumSeverity = default;
        if (!TryResolveRuleId(ruleId, out var resolvedRuleId))
        {
            return false;
        }

        return MinimumSeverities.TryGetValue(resolvedRuleId, out minimumSeverity);
    }

    static bool TryFindRuleIdBySemanticId(string input, out string resolvedRuleId)
    {
        resolvedRuleId = string.Empty;

        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            var candidate = DefaultRuleFactories[i].Id;
            if (!string.Equals(candidate, input, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            resolvedRuleId = candidate;
            return true;
        }

        return false;
    }

    static IReadOnlyDictionary<string, string> BuildCanonicalRuleIdMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            map[$"{CanonicalPrefix}{(i + 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture)}"] = DefaultRuleFactories[i].Id;
        }

        return map;
    }

    static IReadOnlyDictionary<string, string> BuildReverseCanonicalRuleIdMap()
    {
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in CanonicalRuleIdToRuleId)
        {
            reverse[pair.Value] = pair.Key;
        }

        return reverse;
    }

    static IReadOnlySet<string> BuildNonDisableableRuleIdSet()
    {
        return new HashSet<string>(StringComparer.Ordinal)
        {
            "deny-write-all",
        };
    }

    static IReadOnlyDictionary<string, DiagnosticSeverity> BuildMinimumSeverityMap()
    {
        return new Dictionary<string, DiagnosticSeverity>(StringComparer.Ordinal)
        {
            ["deny-write-all"] = DiagnosticSeverity.Error,
        };
    }

    static int ComputeEditDistanceIgnoreCase(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var lc = char.ToLowerInvariant(left[i - 1]);
            for (var j = 1; j <= right.Length; j++)
            {
                var rc = char.ToLowerInvariant(right[j - 1]);
                var substitutionCost = lc == rc ? 0 : 1;
                var deletion = previous[j] + 1;
                var insertion = current[j - 1] + 1;
                var substitution = previous[j - 1] + substitutionCost;

                current[j] = Math.Min(Math.Min(deletion, insertion), substitution);
            }

            var tmp = previous;
            previous = current;
            current = tmp;
        }

        return previous[right.Length];
    }
}
