using Seiton.Core.Parsing;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting;

/// <summary>
/// Normalizes <see cref="RuleConfig"/> properties (trim, dedup, lowercase, registry validation).
/// </summary>
internal static class RuleConfigNormalizer
{
    public static RuleConfig Normalize(RuleConfig config, string ruleId, string filePath, List<Diagnostic> diagnostics)
    {
        var events = NormalizeExtendableList(config.Events, "events extend entry must not be empty", filePath, diagnostics);
        var knownHostedLabels = NormalizeExtendableList(config.KnownHostedLabels, "known-hosted-labels extend entry must not be empty", filePath, diagnostics);
        var publicRegistries = NormalizeRegistryExtendableList(config.PublicRegistries, filePath, diagnostics);
        var untrustedTriggers = NormalizeExtendableList(config.UntrustedTriggers, "untrusted-triggers extend entry must not be empty", filePath, diagnostics);
        var outputCommands = NormalizeExtendableList(config.OutputCommands, "output-commands extend entry must not be empty", filePath, diagnostics);
        var assumeEvents = NormalizeAdditiveValues(config.AssumeEvents, "assume-events entry must not be empty", filePath, diagnostics);
        var allow = NormalizeAdditiveValues(config.Allow, "allow pattern must not be empty", filePath, diagnostics);
        var deny = NormalizeAdditiveValues(config.Deny, "deny pattern must not be empty", filePath, diagnostics);

        return config with
        {
            Events = events,
            KnownHostedLabels = knownHostedLabels,
            PublicRegistries = publicRegistries,
            UntrustedTriggers = untrustedTriggers,
            OutputCommands = outputCommands,
            AssumeEvents = assumeEvents,
            Allow = allow,
            Deny = deny,
        };
    }

    private static ExtendableList? NormalizeExtendableList(
        ExtendableList? list,
        string emptyMessage,
        string filePath,
        List<Diagnostic> diagnostics)
    {
        if (list is null)
        {
            return null;
        }

        var values = NormalizeAdditiveValues(list.Extend, emptyMessage, filePath, diagnostics);
        return values is { Count: > 0 } ? new ExtendableList(values) : null;
    }

    private static ExtendableList? NormalizeRegistryExtendableList(
        ExtendableList? list,
        string filePath,
        List<Diagnostic> diagnostics)
    {
        if (list is null)
        {
            return null;
        }

        var values = NormalizeRegistryHosts(list.Extend, filePath, diagnostics);
        return values is { Count: > 0 } ? new ExtendableList(values) : null;
    }

    private static IReadOnlyList<string>? NormalizeAdditiveValues(
        IReadOnlyList<string>? values,
        string emptyMessage,
        string filePath,
        List<Diagnostic> diagnostics)
    {
        if (values is null || values.Count == 0)
        {
            return null;
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

        return normalized.Count > 0 ? normalized : null;
    }

    private static IReadOnlyList<string> NormalizeRegistryHosts(
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

    private static bool IsValidRegistryHost(string value)
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
}
