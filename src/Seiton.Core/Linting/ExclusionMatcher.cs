using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting;

/// <summary>Matches lint config exclusions against file paths.</summary>
public static class ExclusionMatcher
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="filePath"/> is fully excluded by a file-level
    /// exclusion entry (<c>rules</c> omitted and no <c>jobs</c> scope).
    /// </summary>
    public static bool IsFileFullyExcluded(IReadOnlyList<LintExclusion>? exclusions, string filePath)
    {
        if (exclusions is null || exclusions.Count == 0)
        {
            return false;
        }

        var normalizedFilePath = NormalizePath(filePath);

        for (var i = 0; i < exclusions.Count; i++)
        {
            var exclusion = exclusions[i];
            if (exclusion.Rules is not null || exclusion.Jobs is { Count: > 0 })
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(exclusion.File))
            {
                continue;
            }

            var normalizedPattern = NormalizeExclusionPattern(exclusion.File);
            if (GlobMatch(normalizedPattern, normalizedFilePath))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeExclusionPattern(string pattern)
    {
        var normalized = NormalizePath(pattern);
        if (normalized.Length == 0)
        {
            return normalized;
        }

        if (normalized == "**" || normalized.StartsWith("**/", StringComparison.Ordinal))
        {
            return normalized;
        }

        if (normalized[0] == '/' || (normalized.Length >= 2 && normalized[1] == ':'))
        {
            return normalized;
        }

        return "**/" + normalized;
    }
}
