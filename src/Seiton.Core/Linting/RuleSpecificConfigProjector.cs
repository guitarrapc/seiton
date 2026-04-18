namespace Seiton.Core.Linting;

internal static class RuleSpecificConfigProjector
{
    public static RuleConfig Apply(string ruleId, RuleConfig config)
    {
        var specific = ruleId switch
        {
            "dangerous-triggers" => config.Events?.Extend is { Count: > 0 } values
                ? new DangerousTriggersSpecificConfig(values)
                : RuleSpecificConfig.None,

            "runner-label" => config.KnownHostedLabels?.Extend is { Count: > 0 } values
                ? new RunnerLabelSpecificConfig(values)
                : RuleSpecificConfig.None,

            "credentials" => config.PublicRegistries?.Extend is { Count: > 0 } values
                ? new CredentialsSpecificConfig(values)
                : RuleSpecificConfig.None,

            "cache-poisoning" or "self-hosted-runner" => config.UntrustedTriggers?.Extend is { Count: > 0 } values
                ? new UntrustedTriggersSpecificConfig(values)
                : RuleSpecificConfig.None,

            "unredacted-secrets" => config.OutputCommands?.Extend is { Count: > 0 } values
                ? new UnredactedSecretsSpecificConfig(values)
                : RuleSpecificConfig.None,

            "expr-undefined-var" => config.AssumeEvents is { Count: > 0 } values
                ? new ExprUndefinedVarSpecificConfig(values)
                : RuleSpecificConfig.None,

            "forbidden-uses" => config.Allow is not null || config.Deny is not null
                ? new ForbiddenUsesSpecificConfig(config.Allow, config.Deny)
                : RuleSpecificConfig.None,

            _ => RuleSpecificConfig.None,
        };

        return config with { Specific = specific };
    }
}
