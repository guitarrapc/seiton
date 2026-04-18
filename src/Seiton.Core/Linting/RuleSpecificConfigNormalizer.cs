using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

static class RuleSpecificConfigNormalizer
{
    public static RuleConfig Normalize(RuleConfig config, string ruleId, string filePath, List<Diagnostic> diagnostics)
    {
        RuleSpecificConfig normalized = config.Specific;

        switch (config.Specific)
        {
            case DangerousTriggersSpecificConfig specific when ruleId == "dangerous-triggers":
                {
                    var values = NormalizeAdditiveValues(specific.Events ?? [], "events extend entry must not be empty", filePath, diagnostics);
                    normalized = values.Count == 0 ? RuleSpecificConfig.None : new DangerousTriggersSpecificConfig(values);
                    break;
                }
            case RunnerLabelSpecificConfig specific when ruleId == "runner-label":
                {
                    var values = NormalizeAdditiveValues(specific.KnownHostedLabels ?? [], "known-hosted-labels extend entry must not be empty", filePath, diagnostics);
                    normalized = values.Count == 0 ? RuleSpecificConfig.None : new RunnerLabelSpecificConfig(values);
                    break;
                }
            case CredentialsSpecificConfig specific when ruleId == "credentials":
                {
                    var values = NormalizeRegistryHosts(specific.PublicRegistries ?? [], filePath, diagnostics);
                    normalized = values.Count == 0 ? RuleSpecificConfig.None : new CredentialsSpecificConfig(values);
                    break;
                }
            case UntrustedTriggersSpecificConfig specific when ruleId is "cache-poisoning" or "self-hosted-runner":
                {
                    var values = NormalizeAdditiveValues(specific.UntrustedTriggers ?? [], "untrusted-triggers extend entry must not be empty", filePath, diagnostics);
                    normalized = values.Count == 0 ? RuleSpecificConfig.None : new UntrustedTriggersSpecificConfig(values);
                    break;
                }
            case UnredactedSecretsSpecificConfig specific when ruleId == "unredacted-secrets":
                {
                    var values = NormalizeAdditiveValues(specific.OutputCommands ?? [], "output-commands extend entry must not be empty", filePath, diagnostics);
                    normalized = values.Count == 0 ? RuleSpecificConfig.None : new UnredactedSecretsSpecificConfig(values);
                    break;
                }
            case ExprUndefinedVarSpecificConfig specific when ruleId == "expr-undefined-var":
                {
                    var values = NormalizeAdditiveValues(specific.AssumeEvents ?? [], "assume-events entry must not be empty", filePath, diagnostics);
                    normalized = values.Count == 0 ? RuleSpecificConfig.None : new ExprUndefinedVarSpecificConfig(values);
                    break;
                }
            case ForbiddenUsesSpecificConfig specific when ruleId == "forbidden-uses":
                {
                    var allow = NormalizeAdditiveValues(specific.Allow ?? [], "allow pattern must not be empty", filePath, diagnostics);
                    var deny = NormalizeAdditiveValues(specific.Deny ?? [], "deny pattern must not be empty", filePath, diagnostics);
                    normalized = allow.Count == 0 && deny.Count == 0
                        ? RuleSpecificConfig.None
                        : new ForbiddenUsesSpecificConfig(allow.Count > 0 ? allow : null, deny.Count > 0 ? deny : null);
                    break;
                }
            default:
                {
                    if (ReferenceEquals(config.Specific, RuleSpecificConfig.None))
                    {
                        normalized = RuleSpecificConfig.None;
                        break;
                    }

                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        $"rule '{ruleId}' received mismatched specific config payload '{config.Specific.GetType().Name}'",
                        new TextRange(0, ruleId.Length, 1, 1, 1, 1 + ruleId.Length),
                        FilePath: filePath));
                    normalized = RuleSpecificConfig.None;
                    break;
                }
        }

        return config with { Specific = normalized };
    }

    static IReadOnlyList<string> NormalizeAdditiveValues(
        IReadOnlyList<string> values,
        string emptyMessage,
        string filePath,
        List<Diagnostic> diagnostics)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < values.Count; i++)
        {
            var trimmed = values[i]?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    emptyMessage,
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            var normalizedValue = NormalizeAsciiLower(trimmed);
            if (seen.Add(normalizedValue))
            {
                normalized.Add(normalizedValue);
            }
        }

        return normalized;
    }

    static IReadOnlyList<string> NormalizeRegistryHosts(
        IReadOnlyList<string> values,
        string filePath,
        List<Diagnostic> diagnostics)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < values.Count; i++)
        {
            var trimmed = values[i]?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "credentials additional public registry host must not be empty",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            if (!IsValidRegistryHost(trimmed))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"credentials additional public registry host '{trimmed}' is invalid",
                    new TextRange(0, trimmed.Length, 1, 1, 1, 1 + trimmed.Length),
                    FilePath: filePath));
                continue;
            }

            var normalizedValue = NormalizeAsciiLower(trimmed);
            if (seen.Add(normalizedValue))
            {
                normalized.Add(normalizedValue);
            }
        }

        return normalized;
    }

    static bool IsValidRegistryHost(string value)
    {
        if (value.Contains("://", StringComparison.Ordinal)
            || value.Contains('/')
            || value.Contains('\\'))
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
            {
                return false;
            }
        }

        var colonIndex = value.IndexOf(':');
        if (colonIndex < 0)
        {
            return value.Length > 0;
        }

        if (value.LastIndexOf(':') != colonIndex || colonIndex == 0 || colonIndex == value.Length - 1)
        {
            return false;
        }

        for (var i = colonIndex + 1; i < value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    static string NormalizeAsciiLower(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var buffer = value.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            var ch = buffer[i];
            if (ch is >= 'A' and <= 'Z')
            {
                buffer[i] = (char)(ch + 32);
            }
        }

        return new string(buffer);
    }
}
