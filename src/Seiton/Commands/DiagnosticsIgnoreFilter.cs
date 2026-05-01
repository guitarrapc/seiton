using System.Text.RegularExpressions;
using Seiton.Core.Linting;

namespace Seiton.Commands;

internal static class DiagnosticsIgnoreFilter
{
    internal static Regex[] CompileMessagePatterns(string[] ignore)
    {
        var patterns = new Regex[ignore.Length];
        for (var i = 0; i < ignore.Length; i++)
        {
            patterns[i] = new Regex(
                ignore[i],
                RegexOptions.CultureInvariant | RegexOptions.Compiled,
                LintConfigResourceLimits.IgnoreActionRegexMatchTimeout);
        }

        return patterns;
    }

    /// <returns>True when any pattern matches; timeouts are treated as non-match (keep diagnostic).</returns>
    internal static bool IsMessageIgnored(ReadOnlySpan<Regex> patterns, string message)
    {
        for (var i = 0; i < patterns.Length; i++)
        {
            try
            {
                if (patterns[i].IsMatch(message))
                    return true;
            }
            catch (RegexMatchTimeoutException)
            {
                // Bounded ReDoS: do not suppress the diagnostic when regex cannot decide in time
            }
        }

        return false;
    }
}
