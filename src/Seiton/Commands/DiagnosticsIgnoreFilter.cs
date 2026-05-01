namespace Seiton.Commands;

internal static class DiagnosticsIgnoreFilter
{
    /// <returns>True when any pattern is found as a substring in <paramref name="message"/> (case-insensitive, ordinal).</returns>
    internal static bool IsMessageIgnored(ReadOnlySpan<string> patterns, string message)
    {
        for (var i = 0; i < patterns.Length; i++)
        {
            var pattern = patterns[i];
            if (string.IsNullOrWhiteSpace(pattern))
                continue;
            if (message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
