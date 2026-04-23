using System.Collections.Frozen;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

internal static class RuleCatalog
{
    private const string CanonicalPrefix = "seiton-lint-rule-";

    // Rule responsibilities are intentionally split:
    // - job-structure: cross-key structural constraints on Job shape.
    // - reusable-workflow: uses/with/secrets semantics and forbidden keys in reusable calls.
    // - permissions: scalar/scope value domain validation for permissions.
    // - popular-action-inputs: known-action input-name validation (warning-level).
    private static readonly (RuleId Id, int Priority, Func<IRule> Factory)[] DefaultRuleFactories =
    [
        (RuleId.JobStructure, 0, static () => new JobStructureRule()),
        (RuleId.ReusableWorkflow, 1, static () => new ReusableWorkflowRule()),
        (RuleId.Permissions, 2, static () => new PermissionsRule()),
        (RuleId.PopularActionInputs, 3, static () => new PopularActionInputsRule()),
        (RuleId.UnpinnedUses, 4, static () => new UnpinnedUsesRule()),
        (RuleId.UnpinnedImage, 5, static () => new UnpinnedImageRule()),
        (RuleId.DangerousTriggers, 6, static () => new DangerousTriggersRule()),
        (RuleId.JobPermissionsRequired, 7, static () => new JobPermissionsRequiredRule()),
        (RuleId.NeedsGraph, 8, static () => new NeedsGraphRule()),
        (RuleId.ShellName, 9, static () => new ShellNameRule()),
        (RuleId.RunnerLabel, 10, static () => new RunnerLabelRule()),
        (RuleId.IdNaming, 11, static () => new IdNamingRule()),
        (RuleId.GlobPattern, 12, static () => new GlobPatternRule()),
        (RuleId.DispatchInputs, 13, static () => new DispatchInputsRule()),
        (RuleId.ScheduleEvent, 14, static () => new ScheduleEventRule()),
        (RuleId.DenyWriteAll, 15, static () => new DenyWriteAllRule()),
        (RuleId.Credentials, 16, static () => new CredentialsRule()),
        (RuleId.TemplateInjection, 17, static () => new TemplateInjectionRule()),
        (RuleId.ExprUndefinedVar, 18, static () => new ExprUndefinedVarRule()),
        (RuleId.RunEnvContextDirectUse, 19, static () => new RunEnvContextDirectUseRule()),
        (RuleId.RunnerNoLatest, 20, static () => new RunnerNoLatestRule()),
        (RuleId.RunSecretsContextDirectUse, 21, static () => new RunSecretsContextDirectUseRule()),
        (RuleId.RunInputsContextDirectUse, 22, static () => new RunInputsContextDirectUseRule()),
        (RuleId.SecretsWholeContextAccess, 23, static () => new SecretsWholeContextAccessRule()),
        (RuleId.CheckoutPersistCredentials, 24, static () => new CheckoutPersistCredentialsRule()),
        (RuleId.DenyReadAll, 25, static () => new DenyReadAllRule()),
        (RuleId.DenyInheritSecrets, 26, static () => new DenyInheritSecretsRule()),
        (RuleId.JobTimeoutMinutesRequired, 27, static () => new JobTimeoutMinutesRequiredRule()),
        (RuleId.GitHubAppTokenInputs, 28, static () => new GitHubAppTokenInputsRule()),
        (RuleId.CachePoisoning, 33, static () => new CachePoisoningRule()),
        (RuleId.SelfHostedRunner, 34, static () => new SelfHostedRunnerRule()),
        (RuleId.UnredactedSecrets, 35, static () => new UnredactedSecretsRule()),
        (RuleId.SecretsOutsideEnv, 36, static () => new SecretsOutsideEnvRule()),
        (RuleId.WorkflowSecrets, 37, static () => new WorkflowSecretsRule()),
        (RuleId.JobSecrets, 38, static () => new JobSecretsRule()),
        (RuleId.ActionShellIsRequired, 39, static () => new ActionShellIsRequiredRule()),
        (RuleId.Matrix, 40, static () => new MatrixRule()),
        (RuleId.EnvVar, 41, static () => new EnvVarRule()),
        (RuleId.DeprecatedCommands, 42, static () => new DeprecatedCommandsRule()),
        (RuleId.IfCond, 43, static () => new IfCondRule()),
        (RuleId.FakeTernary, 44, static () => new FakeTernaryRule()),
        (RuleId.ArchivedUses, 45, static () => new ArchivedUsesRule()),
        (RuleId.InsecureCommands, 46, static () => new InsecureCommandsRule()),
        (RuleId.OverprovisionedSecrets, 47, static () => new OverprovisionedSecretsRule()),
        (RuleId.ForbiddenUses, 48, static () => new ForbiddenUsesRule()),
        (RuleId.RefVersionMismatch, 49, static () => new RefVersionMismatchRule()),
        (RuleId.UseTrustedPublishing, 50, static () => new UseTrustedPublishingRule()),
        (RuleId.LocalActionInputs, 51, static () => new LocalActionInputsRule()),
    ];

    // Online rules: opt-in only (disabled by default), participate in WorkflowVisitor
    // traversal for target collection and post-traversal async resolution by OnlineAuditEngine.
    private static readonly (RuleId Id, int Priority, Func<IOnlineRule> Factory)[] OnlineRuleFactories =
    [
        (RuleId.KnownVulnerableActions, 29, static () => new KnownVulnerableActionsRule()),
        (RuleId.ImpostorCommit, 30, static () => new ImpostorCommitRule()),
        (RuleId.RefConfusion, 31, static () => new RefConfusionRule()),
        (RuleId.StaleActionRefs, 32, static () => new StaleActionRefsRule()),
    ];

    private static readonly IReadOnlySet<RuleId> OptInOnlyRuleIds = BuildOptInOnlyRuleIdSet();

    private static readonly (RuleId Id, int Priority)[] AllRuleMetadata = BuildAllRuleMetadata();

    private static readonly IReadOnlyDictionary<string, RuleId> CanonicalRuleIdToRuleId = BuildCanonicalRuleIdMap();

    private static readonly IReadOnlyDictionary<RuleId, string> RuleIdToCanonicalRuleId = BuildReverseCanonicalRuleIdMap();

    private static readonly IReadOnlySet<RuleId> NonDisableableRuleIds = BuildNonDisableableRuleIdSet();

    private static readonly IReadOnlyDictionary<RuleId, DiagnosticSeverity> MinimumSeverities = BuildMinimumSeverityMap();

    private static readonly IReadOnlyDictionary<RuleId, IReadOnlySet<string>> AllowedRuleConfigKeys = BuildAllowedRuleConfigKeys();

    private static readonly FrozenDictionary<string, int> PriorityByRuleIdString = BuildPriorityLookup();

    public static IRule[] CreateDefaultRules()
    {
        var rules = new IRule[DefaultRuleFactories.Length];
        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            rules[i] = DefaultRuleFactories[i].Factory();
        }

        return rules;
    }

    public static IOnlineRule[] CreateOnlineRules()
    {
        var rules = new IOnlineRule[OnlineRuleFactories.Length];
        for (var i = 0; i < OnlineRuleFactories.Length; i++)
        {
            rules[i] = OnlineRuleFactories[i].Factory();
        }

        return rules;
    }

    public static bool IsOptIn(string? ruleId)
    {
        if (string.IsNullOrEmpty(ruleId))
        {
            return false;
        }

        return RuleIdExtensions.TryParse(ruleId, out var parsed) && OptInOnlyRuleIds.Contains(parsed);
    }

    public static int GetPriority(string? ruleId)
    {
        if (string.IsNullOrEmpty(ruleId))
        {
            return int.MaxValue;
        }

        return PriorityByRuleIdString.TryGetValue(ruleId, out var priority) ? priority : int.MaxValue - 1;
    }

    public static bool TryResolveRuleId(string? idOrCanonical, out RuleId resolvedRuleId)
    {
        resolvedRuleId = default;
        if (string.IsNullOrWhiteSpace(idOrCanonical))
        {
            return false;
        }

        if (RuleIdExtensions.TryParse(idOrCanonical, out resolvedRuleId))
        {
            return true;
        }

        if (!CanonicalRuleIdToRuleId.TryGetValue(idOrCanonical, out var mappedRuleId))
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

        if (RuleIdExtensions.TryParse(ruleId, out var parsed) && RuleIdToCanonicalRuleId.TryGetValue(parsed, out var canonical))
        {
            return canonical;
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
            var candidate = AllRuleMetadata[i].Id.ToId();
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

    public static bool IsNonDisableable(RuleId ruleId)
    {
        return NonDisableableRuleIds.Contains(ruleId);
    }

    public static bool TryGetMinimumSeverity(RuleId ruleId, out DiagnosticSeverity minimumSeverity)
    {
        return MinimumSeverities.TryGetValue(ruleId, out minimumSeverity);
    }

    public static bool TryGetAllowedConfigKeys(RuleId ruleId, out IReadOnlySet<string> allowedKeys)
    {
        return AllowedRuleConfigKeys.TryGetValue(ruleId, out allowedKeys!);
    }

    private static IReadOnlyDictionary<string, RuleId> BuildCanonicalRuleIdMap()
    {
        var map = new Dictionary<string, RuleId>(StringComparer.Ordinal);
        for (var i = 0; i < AllRuleMetadata.Length; i++)
        {
            map[$"{CanonicalPrefix}{(i + 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture)}"] = AllRuleMetadata[i].Id;
        }

        return map;
    }

    private static (RuleId Id, int Priority)[] BuildAllRuleMetadata()
    {
        var metadata = new (RuleId Id, int Priority)[DefaultRuleFactories.Length + OnlineRuleFactories.Length];
        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            metadata[i] = (DefaultRuleFactories[i].Id, DefaultRuleFactories[i].Priority);
        }

        for (var i = 0; i < OnlineRuleFactories.Length; i++)
        {
            metadata[DefaultRuleFactories.Length + i] = (OnlineRuleFactories[i].Id, OnlineRuleFactories[i].Priority);
        }

        return metadata;
    }

    private static IReadOnlySet<RuleId> BuildOptInOnlyRuleIdSet()
    {
        var set = new HashSet<RuleId>(OnlineRuleFactories.Length);
        for (var i = 0; i < OnlineRuleFactories.Length; i++)
        {
            set.Add(OnlineRuleFactories[i].Id);
        }

        return set;
    }

    private static FrozenDictionary<string, int> BuildPriorityLookup()
    {
        var dict = new Dictionary<string, int>(AllRuleMetadata.Length, StringComparer.Ordinal);
        for (var i = 0; i < AllRuleMetadata.Length; i++)
        {
            dict[AllRuleMetadata[i].Id.ToId()] = AllRuleMetadata[i].Priority;
        }

        return dict.ToFrozenDictionary(dict.Comparer);
    }

    private static IReadOnlyDictionary<RuleId, string> BuildReverseCanonicalRuleIdMap()
    {
        var reverse = new Dictionary<RuleId, string>();
        foreach (var pair in CanonicalRuleIdToRuleId)
        {
            reverse[pair.Value] = pair.Key;
        }

        return reverse;
    }

    private static IReadOnlySet<RuleId> BuildNonDisableableRuleIdSet()
    {
        return new HashSet<RuleId>
        {
            RuleId.DenyWriteAll,
            RuleId.DenyReadAll,
        };
    }

    private static IReadOnlyDictionary<RuleId, DiagnosticSeverity> BuildMinimumSeverityMap()
    {
        return new Dictionary<RuleId, DiagnosticSeverity>
        {
            [RuleId.DenyWriteAll] = DiagnosticSeverity.Error,
            [RuleId.DenyReadAll] = DiagnosticSeverity.Error,
        };
    }

    private static IReadOnlyDictionary<RuleId, IReadOnlySet<string>> BuildAllowedRuleConfigKeys()
    {
        var empty = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);
        var map = new Dictionary<RuleId, IReadOnlySet<string>>();

        // Pre-build named sets for rules that have specific config keys
        var events = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "events" };
        var knownHostedLabels = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "known-hosted-labels" };
        var publicRegistries = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "public-registries" };
        var untrustedTriggers = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "untrusted-triggers" };
        var outputCommands = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "output-commands" };
        var assumeEvents = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "assume-events" };
        var allowDeny = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "allow", "deny" };
        var secretThresholds = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "max-step-env-secrets", "max-job-secrets" };

        for (var i = 0; i < AllRuleMetadata.Length; i++)
        {
            var id = AllRuleMetadata[i].Id;
            map[id] = id switch
            {
                RuleId.DangerousTriggers => events,
                RuleId.RunnerLabel => knownHostedLabels,
                RuleId.Credentials => publicRegistries,
                RuleId.CachePoisoning => untrustedTriggers,
                RuleId.SelfHostedRunner => untrustedTriggers,
                RuleId.UnredactedSecrets => outputCommands,
                RuleId.ExprUndefinedVar => assumeEvents,
                RuleId.ForbiddenUses => allowDeny,
                RuleId.OverprovisionedSecrets => secretThresholds,
                _ => empty,
            };
        }

        return map;
    }

    private static int ComputeEditDistanceIgnoreCase(string left, string right)
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
