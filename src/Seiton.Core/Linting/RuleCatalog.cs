using Seiton.Core.Linting.Rules;
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
        ("run-env-context-direct-use", 17, static () => new RunEnvContextDirectUseRule()),
        ("runner-no-latest", 18, static () => new RunnerNoLatestRule()),
        ("run-secrets-context-direct-use", 19, static () => new RunSecretsContextDirectUseRule()),
        ("run-inputs-context-direct-use", 20, static () => new RunInputsContextDirectUseRule()),
        ("secrets-whole-context-access", 21, static () => new SecretsWholeContextAccessRule()),
        ("checkout-persist-credentials", 22, static () => new CheckoutPersistCredentialsRule()),
        ("deny-read-all", 23, static () => new DenyReadAllRule()),
        ("deny-inherit-secrets", 24, static () => new DenyInheritSecretsRule()),
        ("job-timeout-minutes-required", 25, static () => new JobTimeoutMinutesRequiredRule()),
        ("github-app-token-inputs", 26, static () => new GitHubAppTokenInputsRule()),
        ("cache-poisoning", 31, static () => new CachePoisoningRule()),
        ("self-hosted-runner", 32, static () => new SelfHostedRunnerRule()),
        ("unredacted-secrets", 33, static () => new UnredactedSecretsRule()),
        ("secrets-outside-env", 34, static () => new SecretsOutsideEnvRule()),
        ("workflow_secrets", 35, static () => new WorkflowSecretsRule()),
        ("job_secrets", 36, static () => new JobSecretsRule()),
        ("action_shell_is_required", 37, static () => new ActionShellIsRequiredRule()),
        ("matrix", 38, static () => new MatrixRule()),
        ("env-var", 39, static () => new EnvVarRule()),
        ("deprecated-commands", 40, static () => new DeprecatedCommandsRule()),
        ("if-cond", 41, static () => new IfCondRule()),
        ("fake-ternary", 42, static () => new FakeTernaryRule()),
        ("deny_job_container_latest_image", 43, static () => new DenyJobContainerLatestImageRule()),
    ];

    static readonly (string Id, int Priority)[] AdditionalRuleMetadata =
    [
        ("known-vulnerable-actions", 27),
        ("impostor-commit", 28),
        ("ref-confusion", 29),
        ("stale-action-refs", 30),
    ];

    static readonly (string Id, int Priority)[] AllRuleMetadata = BuildAllRuleMetadata();

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

        for (var i = 0; i < AllRuleMetadata.Length; i++)
        {
            if (string.Equals(AllRuleMetadata[i].Id, ruleId, StringComparison.Ordinal))
            {
                return AllRuleMetadata[i].Priority;
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
        for (var i = 0; i < AllRuleMetadata.Length; i++)
        {
            var candidate = AllRuleMetadata[i].Id;
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

        for (var i = 0; i < AllRuleMetadata.Length; i++)
        {
            var candidate = AllRuleMetadata[i].Id;
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
        for (var i = 0; i < AllRuleMetadata.Length; i++)
        {
            map[$"{CanonicalPrefix}{(i + 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture)}"] = AllRuleMetadata[i].Id;
        }

        return map;
    }

    static (string Id, int Priority)[] BuildAllRuleMetadata()
    {
        var metadata = new (string Id, int Priority)[DefaultRuleFactories.Length + AdditionalRuleMetadata.Length];
        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            metadata[i] = (DefaultRuleFactories[i].Id, DefaultRuleFactories[i].Priority);
        }

        for (var i = 0; i < AdditionalRuleMetadata.Length; i++)
        {
            metadata[DefaultRuleFactories.Length + i] = AdditionalRuleMetadata[i];
        }

        return metadata;
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
            "deny-read-all",
        };
    }

    static IReadOnlyDictionary<string, DiagnosticSeverity> BuildMinimumSeverityMap()
    {
        return new Dictionary<string, DiagnosticSeverity>(StringComparer.Ordinal)
        {
            ["deny-write-all"] = DiagnosticSeverity.Error,
            ["deny-read-all"] = DiagnosticSeverity.Error,
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
