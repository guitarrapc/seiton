using System.Collections.Frozen;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Linting;

/// <summary>
/// Central registry of all lint rules: factory methods, priorities, policy flags (opt-in),
/// static rule metadata (default severity, auto-fix support) used to populate <see cref="RuleDescriptor"/>,
/// and allowed per-rule configuration keys.
/// </summary>
internal static class RuleCatalog
{

    // Rule responsibilities are intentionally split:
    // - job-structure: cross-key structural constraints on Job shape.
    // - reusable-workflow: uses/with/secrets semantics and forbidden keys in reusable calls.
    // - permissions: scalar/scope value domain validation for permissions.
    // - popular-action-inputs: known-action input-name validation (warning-level).
    // OptIn: false = default-on, true = opt-in only (requires a rules.<id> entry; enabled defaults to true).
    private static readonly (RuleId Id, int Priority, bool OptIn, Func<IRule> Factory)[] DefaultRuleFactories =
    [
        (RuleId.JobStructure, 0, false, static () => new JobStructureRule()),
        (RuleId.ReusableWorkflow, 1, false, static () => new ReusableWorkflowRule()),
        (RuleId.Permissions, 2, false, static () => new PermissionsRule()),
        (RuleId.PopularActionInputs, 3, false, static () => new PopularActionInputsRule()),
        (RuleId.UnpinnedUses, 4, false, static () => new UnpinnedUsesRule()),
        (RuleId.UnpinnedImage, 5, false, static () => new UnpinnedImageRule()),
        (RuleId.DangerousTriggers, 6, false, static () => new DangerousTriggersRule()),
        (RuleId.JobPermissionsRequired, 7, false, static () => new JobPermissionsRequiredRule()),
        (RuleId.NeedsGraph, 8, false, static () => new NeedsGraphRule()),
        (RuleId.ShellName, 9, false, static () => new ShellNameRule()),
        (RuleId.RunnerLabel, 10, false, static () => new RunnerLabelRule()),
        (RuleId.IdNaming, 11, false, static () => new IdNamingRule()),
        (RuleId.GlobPattern, 12, false, static () => new GlobPatternRule()),
        (RuleId.DispatchInputs, 13, false, static () => new DispatchInputsRule()),
        (RuleId.ScheduleEvent, 14, false, static () => new ScheduleEventRule()),
        (RuleId.DenyWriteAll, 15, false, static () => new DenyWriteAllRule()),
        (RuleId.Credentials, 16, false, static () => new CredentialsRule()),
        (RuleId.TemplateInjection, 17, false, static () => new TemplateInjectionRule()),
        (RuleId.ExprUndefinedVar, 18, false, static () => new ExprUndefinedVarRule()),
        (RuleId.RunEnvContextDirectUse, 19, false, static () => new RunEnvContextDirectUseRule()),
        (RuleId.RunnerNoLatest, 20, false, static () => new RunnerNoLatestRule()),
        (RuleId.RunSecretsContextDirectUse, 21, false, static () => new RunSecretsContextDirectUseRule()),
        (RuleId.RunInputsContextDirectUse, 22, false, static () => new RunInputsContextDirectUseRule()),
        (RuleId.SecretsWholeContextAccess, 23, false, static () => new SecretsWholeContextAccessRule()),
        (RuleId.CheckoutPersistCredentials, 24, false, static () => new CheckoutPersistCredentialsRule()),
        (RuleId.DenyReadAll, 25, false, static () => new DenyReadAllRule()),
        (RuleId.DenyInheritSecrets, 26, false, static () => new DenyInheritSecretsRule()),
        (RuleId.JobTimeoutMinutesRequired, 27, false, static () => new JobTimeoutMinutesRequiredRule()),
        (RuleId.GitHubAppTokenInputs, 28, false, static () => new GitHubAppTokenInputsRule()),
        // Priorities 29-32 are reserved for online rules (see OnlineRuleFactories).
        // Keep priorities unique; they determine rule execution order.
        (RuleId.CachePoisoning, 33, false, static () => new CachePoisoningRule()),
        (RuleId.SelfHostedRunner, 34, false, static () => new SelfHostedRunnerRule()),
        (RuleId.UnredactedSecrets, 35, false, static () => new UnredactedSecretsRule()),
        (RuleId.SecretsOutsideEnv, 36, false, static () => new SecretsOutsideEnvRule()),
        (RuleId.WorkflowSecrets, 37, false, static () => new WorkflowSecretsRule()),
        (RuleId.JobSecrets, 38, false, static () => new JobSecretsRule()),
        (RuleId.ActionShellIsRequired, 39, false, static () => new ActionShellIsRequiredRule()),
        (RuleId.Matrix, 40, false, static () => new MatrixRule()),
        (RuleId.EnvVar, 41, false, static () => new EnvVarRule()),
        (RuleId.DeprecatedCommands, 42, false, static () => new DeprecatedCommandsRule()),
        (RuleId.IfCond, 43, false, static () => new IfCondRule()),
        (RuleId.FakeTernary, 44, false, static () => new FakeTernaryRule()),
        (RuleId.ArchivedUses, 45, false, static () => new ArchivedUsesRule()),
        (RuleId.InsecureCommands, 46, false, static () => new InsecureCommandsRule()),
        (RuleId.OverprovisionedSecrets, 47, false, static () => new OverprovisionedSecretsRule()),
        (RuleId.ForbiddenUses, 48, false, static () => new ForbiddenUsesRule()),
        (RuleId.RefVersionMismatch, 49, false, static () => new RefVersionMismatchRule()),
        (RuleId.UseTrustedPublishing, 50, false, static () => new UseTrustedPublishingRule()),
        (RuleId.LocalActionInputs, 51, false, static () => new LocalActionInputsRule()),
        (RuleId.WorkflowCallInputDefault, 52, false, static () => new WorkflowCallInputDefaultRule()),
        (RuleId.OutdatedActionRunner, 53, false, static () => new OutdatedActionRunnerRule()),
        (RuleId.IfExprWrapper, 54, false, static () => new IfExprWrapperRule()),
        (RuleId.ConcurrencyLimits, 55, true, static () => new ConcurrencyLimitsRule()),
        (RuleId.UnsoundCondition, 56, false, static () => new UnsoundConditionRule()),
        (RuleId.UnpinnedTools, 57, false, static () => new UnpinnedToolsRule()),
        (RuleId.UnsoundContains, 58, false, static () => new UnsoundContainsRule()),
        (RuleId.BotConditions, 59, false, static () => new BotConditionsRule()),
        (RuleId.Artipacked, 60, false, static () => new ArtipackedRule()),
        (RuleId.CheckoutUnsafePr, 61, false, static () => new CheckoutUnsafePrRule()),
        (RuleId.BackgroundSteps, 62, false, static () => new BackgroundStepsRule()),
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

    private static readonly IReadOnlyDictionary<RuleId, RuleKeyFlags> AllowedRuleConfigKeys = BuildAllowedRuleConfigKeys();

    private static readonly FrozenDictionary<string, int> PriorityByRuleIdString = BuildPriorityLookup();
    private static readonly Lazy<DefaultActivationMetadata> CachedDefaultActivationMetadata = new(BuildDefaultActivationMetadata);

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

    private static readonly Lazy<RuleDescriptor[]> CachedDescriptors = new(BuildAllRuleDescriptors);

    /// <summary>Returns descriptors for all registered rules (default + online). Result is cached.</summary>
    internal static IReadOnlyList<RuleDescriptor> GetAllRuleDescriptors() => CachedDescriptors.Value;

    internal static bool MatchesDefaultRuleSet(IReadOnlyList<IRule> localRules, IReadOnlyList<IOnlineRule> onlineRules)
    {
        if (localRules.Count != DefaultRuleFactories.Length || onlineRules.Count != OnlineRuleFactories.Length)
        {
            return false;
        }

        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            if (localRules[i].Id != DefaultRuleFactories[i].Id)
            {
                return false;
            }
        }

        for (var i = 0; i < OnlineRuleFactories.Length; i++)
        {
            if (onlineRules[i].Id != OnlineRuleFactories[i].Id)
            {
                return false;
            }
        }

        return true;
    }

    internal static int GetDefaultActiveRuleCount(Parsing.DocumentKind documentKind)
    {
        var metadata = CachedDefaultActivationMetadata.Value;
        return documentKind == Parsing.DocumentKind.ActionMetadata
            ? metadata.ActionMetadataActiveRuleCount
            : metadata.WorkflowActiveRuleCount;
    }

    internal static string[] GetDefaultDisabledRuleIds(Parsing.DocumentKind documentKind)
    {
        var metadata = CachedDefaultActivationMetadata.Value;
        return documentKind == Parsing.DocumentKind.ActionMetadata
            ? metadata.ActionMetadataDisabledRuleIds
            : metadata.WorkflowDisabledRuleIds;
    }

    private static RuleDescriptor[] BuildAllRuleDescriptors()
    {
        var descriptors = new RuleDescriptor[DefaultRuleFactories.Length + OnlineRuleFactories.Length];

        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            var entry = DefaultRuleFactories[i];
            var rule = entry.Factory();
            descriptors[i] = new RuleDescriptor(
                entry.Id.ToId(),
                rule.Name,
                entry.OptIn,
                IsOnline: false,
                rule.SupportsDocumentKind(Parsing.DocumentKind.Workflow),
                rule.SupportsDocumentKind(Parsing.DocumentKind.ActionMetadata),
                GetDefaultSeverity(entry.Id),
                GetSupportsAutoFix(entry.Id));
        }

        for (var i = 0; i < OnlineRuleFactories.Length; i++)
        {
            var entry = OnlineRuleFactories[i];
            var rule = entry.Factory();
            descriptors[DefaultRuleFactories.Length + i] = new RuleDescriptor(
                entry.Id.ToId(),
                rule.Name,
                IsOptIn: true,
                IsOnline: true,
                rule.SupportsDocumentKind(Parsing.DocumentKind.Workflow),
                rule.SupportsDocumentKind(Parsing.DocumentKind.ActionMetadata),
                GetDefaultSeverity(entry.Id),
                GetSupportsAutoFix(entry.Id));
        }

        return descriptors;
    }

    private static DefaultActivationMetadata BuildDefaultActivationMetadata()
    {
        var descriptors = GetAllRuleDescriptors();
        var workflowActiveRuleCount = 0;
        var actionMetadataActiveRuleCount = 0;
        var workflowDisabledRuleIds = new List<string>(8);
        var actionMetadataDisabledRuleIds = new List<string>(8);

        for (var i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];
            if (descriptor.IsOptIn)
            {
                if (descriptor.SupportsWorkflow)
                {
                    workflowDisabledRuleIds.Add(descriptor.Id);
                }

                if (descriptor.SupportsAction)
                {
                    actionMetadataDisabledRuleIds.Add(descriptor.Id);
                }

                continue;
            }

            if (descriptor.SupportsWorkflow)
            {
                workflowActiveRuleCount++;
            }

            if (descriptor.SupportsAction)
            {
                actionMetadataActiveRuleCount++;
            }
        }

        return new DefaultActivationMetadata(
            workflowActiveRuleCount,
            actionMetadataActiveRuleCount,
            workflowDisabledRuleIds.ToArray(),
            actionMetadataDisabledRuleIds.ToArray());
    }

    /// <summary>Returns the normative default severity for a rule: "error", "warning", "info", or "mixed".</summary>
    private static string GetDefaultSeverity(RuleId ruleId) => ruleId switch
    {
        RuleId.JobStructure => "error",
        RuleId.ReusableWorkflow => "error",
        RuleId.Permissions => "mixed",
        RuleId.PopularActionInputs => "warning",
        RuleId.UnpinnedUses => "mixed",
        RuleId.UnpinnedImage => "warning",
        RuleId.DangerousTriggers => "warning",
        RuleId.JobPermissionsRequired => "warning",
        RuleId.NeedsGraph => "error",
        RuleId.ShellName => "mixed",
        RuleId.RunnerLabel => "mixed",
        RuleId.IdNaming => "error",
        RuleId.GlobPattern => "error",
        RuleId.DispatchInputs => "error",
        RuleId.ScheduleEvent => "error",
        RuleId.DenyWriteAll => "error",
        RuleId.Credentials => "mixed",
        RuleId.TemplateInjection => "error",
        RuleId.ExprUndefinedVar => "error",
        RuleId.RunEnvContextDirectUse => "error",
        RuleId.RunnerNoLatest => "warning",
        RuleId.RunSecretsContextDirectUse => "error",
        RuleId.RunInputsContextDirectUse => "error",
        RuleId.SecretsWholeContextAccess => "error",
        RuleId.CheckoutPersistCredentials => "warning",
        RuleId.CheckoutUnsafePr => "warning",
        RuleId.DenyReadAll => "error",
        RuleId.DenyInheritSecrets => "error",
        RuleId.JobTimeoutMinutesRequired => "error",
        RuleId.GitHubAppTokenInputs => "error",
        RuleId.KnownVulnerableActions => "error",
        RuleId.ImpostorCommit => "error",
        RuleId.RefConfusion => "error",
        RuleId.StaleActionRefs => "warning",
        RuleId.CachePoisoning => "warning",
        RuleId.SelfHostedRunner => "warning",
        RuleId.UnredactedSecrets => "warning",
        RuleId.SecretsOutsideEnv => "warning",
        RuleId.WorkflowSecrets => "error",
        RuleId.JobSecrets => "error",
        RuleId.ActionShellIsRequired => "error",
        RuleId.Matrix => "warning",
        RuleId.EnvVar => "warning",
        RuleId.DeprecatedCommands => "warning",
        RuleId.IfCond => "warning",
        RuleId.FakeTernary => "warning",
        RuleId.ArchivedUses => "warning",
        RuleId.InsecureCommands => "warning",
        RuleId.OverprovisionedSecrets => "warning",
        RuleId.ForbiddenUses => "warning",
        RuleId.RefVersionMismatch => "warning",
        RuleId.UseTrustedPublishing => "warning",
        RuleId.LocalActionInputs => "mixed",
        RuleId.WorkflowCallInputDefault => "error",
        RuleId.OutdatedActionRunner => "error",
        RuleId.IfExprWrapper => "warning",
        RuleId.ConcurrencyLimits => "warning",
        RuleId.UnsoundCondition => "warning",
        RuleId.UnpinnedTools => "warning",
        RuleId.UnsoundContains => "mixed",
        RuleId.BotConditions => "mixed",
        RuleId.Artipacked => "mixed",
        RuleId.BackgroundSteps => "mixed",
        _ => throw new ArgumentOutOfRangeException(nameof(ruleId), ruleId, "No default severity defined for this rule."),
    };

    /// <summary>Returns whether the rule can produce auto-fix suggestions.</summary>
    private static bool GetSupportsAutoFix(RuleId ruleId) => ruleId switch
    {
        RuleId.TemplateInjection => true,
        RuleId.UnpinnedUses => true,
        RuleId.UnpinnedImage => true,
        RuleId.CheckoutPersistCredentials => true,
        RuleId.CheckoutUnsafePr => true,
        RuleId.DenyWriteAll => true,
        RuleId.DenyReadAll => true,
        RuleId.IdNaming => true,
        RuleId.PopularActionInputs => true,
        RuleId.RunEnvContextDirectUse => true,
        RuleId.RunSecretsContextDirectUse => true,
        RuleId.RunInputsContextDirectUse => true,
        RuleId.JobPermissionsRequired => true,
        RuleId.JobTimeoutMinutesRequired => true,
        RuleId.IfExprWrapper => true,
        RuleId.UnsoundCondition => true,
        RuleId.RunnerNoLatest => true,
        _ => false,
    };

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

        const int maxDistance = 4;
        var bestCandidate = string.Empty;
        var bestDistance = maxDistance + 1;
        for (var i = 0; i < AllRuleMetadata.Length; i++)
        {
            var candidate = AllRuleMetadata[i].Id.ToId();
            var distance = EditDistance.ComputeIgnoreCase(input, candidate, maxDistance);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            bestCandidate = candidate;
        }

        return bestDistance <= maxDistance ? bestCandidate : null;
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
        var set = new HashSet<RuleId>();

        // Local rules marked as opt-in.
        for (var i = 0; i < DefaultRuleFactories.Length; i++)
        {
            if (DefaultRuleFactories[i].OptIn)
            {
                set.Add(DefaultRuleFactories[i].Id);
            }
        }

        // Online rules are always opt-in.
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
                RuleId.RunnerNoLatest => RuleKeyFlags.FixMapping,
                RuleId.BotConditions => RuleKeyFlags.StrictDetection,
                RuleId.RunEnvContextDirectUse => RuleKeyFlags.Strict,
                RuleId.RunInputsContextDirectUse => RuleKeyFlags.Strict,
                _ => RuleKeyFlags.None,
            };
        }

        return map;
    }

    private readonly record struct DefaultActivationMetadata(
        int WorkflowActiveRuleCount,
        int ActionMetadataActiveRuleCount,
        string[] WorkflowDisabledRuleIds,
        string[] ActionMetadataDisabledRuleIds);

}
