using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting;

/// <summary>Matches lint config exclusions against file paths.</summary>
public static class ExclusionMatcher
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="workflowFilePath"/> matches an exclusion <c>file</c> glob pattern.
    /// </summary>
    public static bool MatchesWorkflowFile(string filePattern, string workflowFilePath)
    {
        if (string.IsNullOrWhiteSpace(filePattern))
        {
            return false;
        }

        var normalizedPattern = NormalizeExclusionPattern(filePattern.Trim());
        var normalizedFilePath = NormalizePath(workflowFilePath);
        return GlobMatch(normalizedPattern, normalizedFilePath);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="filePath"/> is fully excluded by a file-level
    /// exclusion entry (<c>rules</c> omitted or <c>rules: ["*"]</c>, and no <c>jobs</c> scope).
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
            if (exclusion.Jobs is { Count: > 0 })
            {
                continue;
            }

            if (exclusion.Rules is not null && !ExclusionNormalizer.IsAllRulesWildcard(exclusion.Rules))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(exclusion.File))
            {
                continue;
            }

            if (MatchesWorkflowFile(exclusion.File, normalizedFilePath))
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
