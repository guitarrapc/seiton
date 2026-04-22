using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>
/// Builds <see cref="RuleSpecificConfig"/> from parsed rule-option keys (shared between YAML DOM and tests).
/// </summary>
internal static class LintConfigRuleBodyMaterializer
{
    public static RuleSpecificConfig BuildSpecific(
        string ruleId,
        IReadOnlySet<string> seenRuleSpecificKeys,
        IReadOnlyList<string> events,
        IReadOnlyList<string> knownHostedLabels,
        IReadOnlyList<string> publicRegistries,
        IReadOnlyList<string> untrustedTriggers,
        IReadOnlyList<string> outputCommands,
        IReadOnlyList<string> assumeEvents,
        IReadOnlyList<string> allow,
        IReadOnlyList<string> deny,
        int? maxStepEnvSecrets,
        int? maxJobSecrets,
        int ruleLineNumber,
        List<Diagnostic> diagnostics,
        string filePath)
    {
        if (!RuleCatalog.TryResolveRuleId(ruleId, out var resolvedRuleId))
        {
            return RuleSpecificConfig.None;
        }

        if (RuleCatalog.TryGetAllowedConfigKeys(resolvedRuleId, out var allowed))
        {
            foreach (var key in seenRuleSpecificKeys)
            {
                if (!allowed.Contains(key))
                {
                    diagnostics.Add(CreateDiag(
                        $"rule '{resolvedRuleId}' does not accept '{key}' config key",
                        ruleLineNumber,
                        3,
                        key.Length,
                        filePath));
                }
            }
        }

        return resolvedRuleId switch
        {
            "dangerous-triggers" when events is { Count: > 0 } => new DangerousTriggersSpecificConfig(events),
            "runner-label" when knownHostedLabels is { Count: > 0 } => new RunnerLabelSpecificConfig(knownHostedLabels),
            "credentials" when publicRegistries is { Count: > 0 } => new CredentialsSpecificConfig(publicRegistries),
            "cache-poisoning" or "self-hosted-runner" when untrustedTriggers is { Count: > 0 } => new UntrustedTriggersSpecificConfig(untrustedTriggers),
            "unredacted-secrets" when outputCommands is { Count: > 0 } => new UnredactedSecretsSpecificConfig(outputCommands),
            "expr-undefined-var" when assumeEvents is { Count: > 0 } => new ExprUndefinedVarSpecificConfig(assumeEvents),
            "forbidden-uses" when allow.Count > 0 || deny.Count > 0 => new ForbiddenUsesSpecificConfig(allow.Count > 0 ? allow : null, deny.Count > 0 ? deny : null),
            "overprovisioned-secrets" when maxStepEnvSecrets is not null || maxJobSecrets is not null => new OverprovisionedSecretsSpecificConfig(
                maxStepEnvSecrets ?? 5,
                maxJobSecrets ?? 5),
            _ => RuleSpecificConfig.None,
        };
    }

    private static Diagnostic CreateDiag(string message, int line, int column, int length, string filePath)
    {
        var safeLength = Math.Max(length, 1);
        return new Diagnostic(
            DiagnosticSeverity.Error,
            message,
            new TextRange(0, safeLength, line, column, line, column + safeLength),
            FilePath: filePath);
    }
}
