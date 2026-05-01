namespace Seiton.Commands;

internal static class DiagnosticsIgnoreFilter
{
    /// <returns>True when any pattern is found as a substring in <paramref name="message"/> (case-insensitive, ordinal).</returns>
    internal static bool IsMessageIgnored(ReadOnlySpan<string> patterns, string message)
    {
        for (var i = 0; i < patterns.Length; i++)
        {
            if (message.Contains(patterns[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
