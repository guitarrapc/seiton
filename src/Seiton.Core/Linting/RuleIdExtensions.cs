using System.Collections.Frozen;

namespace Seiton.Core.Linting;

/// <summary>Conversion methods between <see cref="RuleId"/> enum values and their kebab-case string identifiers.</summary>
internal static class RuleIdExtensions
{
    private static readonly FrozenDictionary<string, RuleId> NameToRuleId = BuildNameToRuleId();

    /// <summary>Converts a <see cref="RuleId"/> enum value to its kebab-case string identifier.</summary>
    public static string ToId(this RuleId id) => id switch
    {
        RuleId.JobStructure => "job-structure",
        RuleId.ReusableWorkflow => "reusable-workflow",
        RuleId.Permissions => "permissions",
        RuleId.PopularActionInputs => "popular-action-inputs",
        RuleId.UnpinnedUses => "unpinned-uses",
        RuleId.UnpinnedImage => "unpinned-image",
        RuleId.DangerousTriggers => "dangerous-triggers",
        RuleId.JobPermissionsRequired => "job-permissions-required",
        RuleId.NeedsGraph => "needs-graph",
        RuleId.ShellName => "shell-name",
        RuleId.RunnerLabel => "runner-label",
        RuleId.IdNaming => "id-naming",
        RuleId.GlobPattern => "glob-pattern",
        RuleId.DispatchInputs => "dispatch-inputs",
        RuleId.ScheduleEvent => "schedule-event",
        RuleId.DenyWriteAll => "deny-write-all",
        RuleId.Credentials => "credentials",
        RuleId.TemplateInjection => "template-injection",
        RuleId.ExprUndefinedVar => "expr-undefined-var",
        RuleId.RunEnvContextDirectUse => "run-env-context-direct-use",
        RuleId.RunnerNoLatest => "runner-no-latest",
        RuleId.RunSecretsContextDirectUse => "run-secrets-context-direct-use",
        RuleId.RunInputsContextDirectUse => "run-inputs-context-direct-use",
        RuleId.SecretsWholeContextAccess => "secrets-whole-context-access",
        RuleId.CheckoutPersistCredentials => "checkout-persist-credentials",
        RuleId.DenyReadAll => "deny-read-all",
        RuleId.DenyInheritSecrets => "deny-inherit-secrets",
        RuleId.JobTimeoutMinutesRequired => "job-timeout-minutes-required",
        RuleId.GitHubAppTokenInputs => "github-app-token-inputs",
        RuleId.KnownVulnerableActions => "known-vulnerable-actions",
        RuleId.ImpostorCommit => "impostor-commit",
        RuleId.RefConfusion => "ref-confusion",
        RuleId.StaleActionRefs => "stale-action-refs",
        RuleId.CachePoisoning => "cache-poisoning",
        RuleId.SelfHostedRunner => "self-hosted-runner",
        RuleId.UnredactedSecrets => "unredacted-secrets",
        RuleId.SecretsOutsideEnv => "secrets-outside-env",
        RuleId.WorkflowSecrets => "workflow-secrets",
        RuleId.JobSecrets => "job-secrets",
        RuleId.ActionShellIsRequired => "action-shell-is-required",
        RuleId.Matrix => "matrix",
        RuleId.EnvVar => "env-var",
        RuleId.DeprecatedCommands => "deprecated-commands",
        RuleId.IfCond => "if-cond",
        RuleId.FakeTernary => "fake-ternary",
        RuleId.ArchivedUses => "archived-uses",
        RuleId.InsecureCommands => "insecure-commands",
        RuleId.OverprovisionedSecrets => "overprovisioned-secrets",
        RuleId.ForbiddenUses => "forbidden-uses",
        RuleId.RefVersionMismatch => "ref-version-mismatch",
        RuleId.UseTrustedPublishing => "use-trusted-publishing",
        RuleId.LocalActionInputs => "local-action-inputs",
        RuleId.WorkflowCallInputDefault => "workflow-call-input-default",
        RuleId.OutdatedActionRunner => "outdated-action-runner",
        RuleId.IfExprWrapper => "if-expr-wrapper",
        RuleId.ConcurrencyLimits => "concurrency-limits",
        RuleId.UnsoundCondition => "unsound-condition",
        RuleId.UnpinnedTools => "unpinned-tools",
        RuleId.UnsoundContains => "unsound-contains",
        RuleId.BotConditions => "bot-conditions",
        RuleId.Syntax => "syntax",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    /// <summary>Attempts to parse a kebab-case string into a <see cref="RuleId"/> enum value.</summary>
    public static bool TryParse(string value, out RuleId ruleId)
    {
        return NameToRuleId.TryGetValue(value, out ruleId);
    }

    private static FrozenDictionary<string, RuleId> BuildNameToRuleId()
    {
        var values = Enum.GetValues<RuleId>();
        var dict = new Dictionary<string, RuleId>(values.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var id in values)
        {
            dict[id.ToId()] = id;
        }

        return dict.ToFrozenDictionary(dict.Comparer);
    }
}
