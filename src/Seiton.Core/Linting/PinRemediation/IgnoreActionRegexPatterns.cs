using System.Text.RegularExpressions;

namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// Regex compilation shared by pinning and online audit for <see cref="IgnoreActionEntry"/> (<c>fix.pinning.ignore-actions</c> YAML).
/// </summary>
internal static class IgnoreActionRegexPatterns
{
    internal static Regex Compile(string pattern) =>
        new(
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.Compiled,
            LintConfigResourceLimits.IgnoreActionRegexMatchTimeout);
}
