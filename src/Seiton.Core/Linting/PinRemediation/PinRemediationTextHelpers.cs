namespace Seiton.Core.Linting.PinRemediation;

internal static class PinRemediationTextHelpers
{
    internal static bool ContainsExact(IReadOnlyList<string> values, string target)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], target, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static string AppendHelp(string? originalHelp, string skipReason)
    {
        if (string.IsNullOrWhiteSpace(originalHelp))
        {
            return skipReason;
        }

        if (string.Equals(originalHelp, skipReason, StringComparison.Ordinal))
        {
            return originalHelp;
        }

        return string.Concat(originalHelp, "\n", skipReason);
    }
}
