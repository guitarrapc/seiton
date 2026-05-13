using System.Collections.Frozen;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>
/// Central registry of all lint rules: factory methods, priorities, policy flags (non-disableable, opt-in),
/// minimum severities, and allowed per-rule configuration keys.
/// </summary>
internal static class RuleCatalog
{

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
        // Priorities 29-32 are reserved for online rules (see OnlineRuleFactories).
        // Keep priorities unique; they determine rule execution order.
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
        (RuleId.WorkflowCallInputDefault, 52, static () => new WorkflowCallInputDefaultRule()),
        (RuleId.OutdatedActionRunner, 53, static () => new OutdatedActionRunnerRule()),
        (RuleId.IfExprWrapper, 54, static () => new IfExprWrapperRule()),
        (RuleId.ConcurrencyLimits, 55, static () => new ConcurrencyLimitsRule()),
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

    private static readonly IReadOnlySet<RuleId> NonDisableableRuleIds = BuildNonDisableableRuleIdSet();

    private static readonly IReadOnlyDictionary<RuleId, DiagnosticSeverity> MinimumSeverities = BuildMinimumSeverityMap();

    private static readonly IReadOnlyDictionary<RuleId, RuleKeyFlags> AllowedRuleConfigKeys = BuildAllowedRuleConfigKeys();

    private static readonly FrozenDictionary<string, int> PriorityByRuleIdString = BuildPriorityLookup();

    /// <summary>Creates a new array of all default (non-online) rule instances.</summary>
    public static IRule[] CreateDefaultRules()
    {
        var rules = new IRule[DefaultRuleFactories.Length];
        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            rules[i] = DefaultRuleFactories[i].Factory();
        }

        return rules;
    }

    /// <summary>Creates a new array of all online rule instances.</summary>
    public static IOnlineRule[] CreateOnlineRules()
    {
        var rules = new IOnlineRule[OnlineRuleFactories.Length];
        for (var i = 0; i < OnlineRuleFactories.Length; i++)
        {
            rules[i] = OnlineRuleFactories[i].Factory();
        }

        return rules;
    }

    /// <summary>Returns whether the specified rule is opt-in only (disabled by default).</summary>
    public static bool IsOptIn(string? ruleId)
    {
        if (string.IsNullOrEmpty(ruleId))
        {
            return false;
        }

        return RuleIdExtensions.TryParse(ruleId, out var parsed) && OptInOnlyRuleIds.Contains(parsed);
    }

    /// <summary>Returns the priority of the rule (lower values run first). Returns <see cref="int.MaxValue"/> for unknown IDs.</summary>
    public static int GetPriority(string? ruleId)
    {
        if (string.IsNullOrEmpty(ruleId))
        {
            return int.MaxValue;
        }

        return PriorityByRuleIdString.TryGetValue(ruleId, out var priority) ? priority : int.MaxValue - 1;
    }

    /// <summary>Resolves a kebab-case semantic rule ID (e.g. <c>job-permissions-required</c>) to a <see cref="RuleId"/>.</summary>
    public static bool TryResolveRuleId(string? ruleId, out RuleId resolvedRuleId)
    {
        resolvedRuleId = default;
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return false;
        }

        return RuleIdExtensions.TryParse(ruleId, out resolvedRuleId);
    }

    /// <summary>Suggests a similar rule ID for a possible typo, or returns <c>null</c> if no close match is found.</summary>
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
            var distance = EditDistance.ComputeIgnoreCase(input, candidate);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            bestCandidate = candidate;
        }

        return bestDistance <= 4 ? bestCandidate : null;
    }

    /// <summary>Returns whether the specified rule cannot be disabled by user configuration.</summary>
    public static bool IsNonDisableable(RuleId ruleId)
    {
        return NonDisableableRuleIds.Contains(ruleId);
    }

    /// <summary>Gets the minimum severity enforced for the specified rule, if any.</summary>
    public static bool TryGetMinimumSeverity(RuleId ruleId, out DiagnosticSeverity minimumSeverity)
    {
        return MinimumSeverities.TryGetValue(ruleId, out minimumSeverity);
    }

    /// <summary>Gets the set of allowed rule-specific configuration keys for the specified rule.</summary>
    public static bool TryGetAllowedConfigKeys(RuleId ruleId, out RuleKeyFlags allowedKeys)
    {
        return AllowedRuleConfigKeys.TryGetValue(ruleId, out allowedKeys);
    }

    private static (RuleId Id, int Priority)[] BuildAllRuleMetadata()
    {
        var metadata = new (RuleId Id, int Priority)[DefaultRuleFactories.Length + OnlineRuleFactories.Length];
        var seen = new HashSet<int>(metadata.Length);
        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            metadata[i] = (DefaultRuleFactories[i].Id, DefaultRuleFactories[i].Priority);
            if (!seen.Add(DefaultRuleFactories[i].Priority))
            {
                throw new InvalidOperationException(
                    $"Duplicate rule priority {DefaultRuleFactories[i].Priority} detected for rule '{DefaultRuleFactories[i].Id}'. Priorities must be unique.");
            }
        }

        for (var i = 0; i < OnlineRuleFactories.Length; i++)
        {
            metadata[DefaultRuleFactories.Length + i] = (OnlineRuleFactories[i].Id, OnlineRuleFactories[i].Priority);
            if (!seen.Add(OnlineRuleFactories[i].Priority))
            {
                throw new InvalidOperationException(
                    $"Duplicate rule priority {OnlineRuleFactories[i].Priority} detected for rule '{OnlineRuleFactories[i].Id}'. Priorities must be unique.");
            }
        }

        return metadata;
    }

    private static IReadOnlySet<RuleId> BuildOptInOnlyRuleIdSet()
    {
        var set = new HashSet<RuleId>(OnlineRuleFactories.Length + 1);
        for (var i = 0; i < OnlineRuleFactories.Length; i++)
        {
            set.Add(OnlineRuleFactories[i].Id);
        }

        // Local rules that are opt-in only (disabled by default).
        set.Add(RuleId.ConcurrencyLimits);

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

    private static IReadOnlyDictionary<RuleId, RuleKeyFlags> BuildAllowedRuleConfigKeys()
    {
        var map = new Dictionary<RuleId, RuleKeyFlags>();

        for (var i = 0; i < AllRuleMetadata.Length; i++)
        {
            var id = AllRuleMetadata[i].Id;
            map[id] = id switch
            {
                RuleId.DangerousTriggers => RuleKeyFlags.Events,
                RuleId.RunnerLabel => RuleKeyFlags.KnownHostedLabels,
                RuleId.Credentials => RuleKeyFlags.PublicRegistries,
                RuleId.CachePoisoning => RuleKeyFlags.UntrustedTriggers,
                RuleId.SelfHostedRunner => RuleKeyFlags.UntrustedTriggers,
                RuleId.UnredactedSecrets => RuleKeyFlags.OutputCommands,
                RuleId.ExprUndefinedVar => RuleKeyFlags.AssumeEvents,
                RuleId.ForbiddenUses => RuleKeyFlags.Allow | RuleKeyFlags.Deny,
                RuleId.UnpinnedUses => RuleKeyFlags.IgnoreActions,
                RuleId.OverprovisionedSecrets => RuleKeyFlags.MaxStepEnvSecrets | RuleKeyFlags.MaxJobSecrets,
                _ => RuleKeyFlags.None,
            };
        }

        return map;
    }

}
