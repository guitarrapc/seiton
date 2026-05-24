using Seiton.Core.Parsing;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting;

/// <summary>
/// Normalizes <see cref="RuleConfig"/> properties (trim, dedup, lowercase, registry validation).
/// </summary>
internal static class RuleConfigNormalizer
{
    /// <summary>
    /// Normalizes all non-null rule-specific properties (trim, dedup, lowercase, registry validation) and emits diagnostics for invalid entries.
    /// </summary>
    public static RuleConfig Normalize(RuleConfig config, string filePath, List<Diagnostic> diagnostics)
    {
        var events = NormalizeAdditiveList(config.Events, "events entry must not be empty", filePath, diagnostics);
        var knownHostedLabels = NormalizeAdditiveList(config.KnownHostedLabels, "known-hosted-labels entry must not be empty", filePath, diagnostics);
        var publicRegistries = NormalizeRegistryList(config.PublicRegistries, filePath, diagnostics);
        var untrustedTriggers = NormalizeAdditiveList(config.UntrustedTriggers, "untrusted-triggers entry must not be empty", filePath, diagnostics);
        var outputCommands = NormalizeAdditiveList(config.OutputCommands, "output-commands entry must not be empty", filePath, diagnostics);
        var assumeEvents = NormalizeAdditiveValues(config.AssumeEvents, "assume-events entry must not be empty", filePath, diagnostics);
        var allow = NormalizeAdditiveValues(config.Allow, "allow pattern must not be empty", filePath, diagnostics);
        var deny = NormalizeAdditiveValues(config.Deny, "deny pattern must not be empty", filePath, diagnostics);
        var ignoreActions = NormalizeIgnoreActions(config.IgnoreActions, filePath, diagnostics);

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
            IgnoreActions = ignoreActions,
        };
    }

    private static IReadOnlyList<IgnoreActionRule>? NormalizeIgnoreActions(
        IReadOnlyList<IgnoreActionRule>? values,
        string filePath,
        List<Diagnostic> diagnostics)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var normalized = new List<IgnoreActionRule>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < values.Count; i++)
        {
            var entry = values[i];
            var trimmedPattern = entry.Pattern?.Trim() ?? string.Empty;
            if (trimmedPattern.Length == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "ignore-actions pattern must not be empty",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            var normalizedPattern = NormalizeAsciiLower(trimmedPattern);
            if (entry.Refs is null)
            {
                if (seen.Add(normalizedPattern))
                {
                    normalized.Add(new IgnoreActionRule(normalizedPattern));
                }

                continue;
            }

            if (entry.Refs.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "ignore-actions requires non-empty 'refs' list when 'refs' is present",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            var normalizedRefs = new List<string>(entry.Refs.Count);
            var seenRefs = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < entry.Refs.Count; j++)
            {
                var trimmedRef = entry.Refs[j]?.Trim() ?? string.Empty;
                if (trimmedRef.Length == 0)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        "ignore-actions ref entries must not be empty",
                        new TextRange(0, 1, 1, 1, 1, 2),
                        FilePath: filePath));
                    continue;
                }

                if (seenRefs.Add(trimmedRef))
                {
                    normalizedRefs.Add(trimmedRef);
                }
            }

            if (normalizedRefs.Count == 0)
            {
                continue;
            }

            var dedupKey = string.Concat(normalizedPattern, "\n", string.Join("\n", normalizedRefs));
            if (seen.Add(dedupKey))
            {
                normalized.Add(new IgnoreActionRule(normalizedPattern, normalizedRefs));
            }
        }

        return normalized.Count > 0 ? normalized : null;
    }

    private static IReadOnlyList<string>? NormalizeAdditiveList(
        IReadOnlyList<string>? list,
        string emptyMessage,
        string filePath,
        List<Diagnostic> diagnostics)
    {
        if (list is null)
        {
            return null;
        }

        var values = NormalizeAdditiveValues(list, emptyMessage, filePath, diagnostics);
        return values is { Count: > 0 } ? values : null;
    }

    private static IReadOnlyList<string>? NormalizeRegistryList(
        IReadOnlyList<string>? list,
        string filePath,
        List<Diagnostic> diagnostics)
    {
        if (list is null)
        {
            return null;
        }

        var values = NormalizeRegistryHosts(list, filePath, diagnostics);
        return values is { Count: > 0 } ? values : null;
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
