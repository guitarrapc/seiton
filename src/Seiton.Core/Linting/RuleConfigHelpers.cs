namespace Seiton.Core.Linting;

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
