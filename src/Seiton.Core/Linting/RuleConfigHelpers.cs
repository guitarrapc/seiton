namespace Seiton.Core.Linting;

/// <summary>Shared utilities for building normalized string sets from rule configuration lists.</summary>
internal static class RuleConfigHelpers
{
    internal static HashSet<string> BuildNormalizedSet(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        return new HashSet<string>(values, StringComparer.Ordinal);
    }
}
