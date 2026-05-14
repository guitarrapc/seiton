namespace Seiton.Core.Parsing;

/// <summary>
/// Provides "did you mean?" suggestions using Levenshtein distance.
/// Used only on error paths — allocation is acceptable here.
/// </summary>
internal static class SuggestionHelper
{
    /// <summary>
    /// Finds the closest match from <paramref name="candidates"/> for the given <paramref name="input"/>.
    /// Returns null if no candidate is within an acceptable distance.
    /// </summary>
    public static string? FindClosest(string input, ReadOnlySpan<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var distance = LevenshteinDistance(input, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best is not null && IsDistanceAcceptable(input.Length, bestDistance) ? best : null;
    }

    /// <summary>
    /// Finds the closest match from <paramref name="candidates"/> for the given <paramref name="input"/>.
    /// Uses foreach enumeration — zero-allocation for FrozenSet (struct enumerator).
    /// </summary>
    public static string? FindClosest(string input, IReadOnlyCollection<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var distance = LevenshteinDistance(input, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best is not null && IsDistanceAcceptable(input.Length, bestDistance) ? best : null;
    }

    private static bool IsDistanceAcceptable(int inputLength, int distance)
    {
        var threshold = inputLength switch
        {
            <= 4 => 1,
            <= 8 => 2,
            _ => 3,
        };

        return distance <= threshold;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var lc = char.ToLowerInvariant(a[i - 1]);
            for (var j = 1; j <= b.Length; j++)
            {
                var rc = char.ToLowerInvariant(b[j - 1]);
                var cost = lc == rc ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    /// <summary>
    /// Formats an array of option names as a quoted, comma-separated list for diagnostic messages.
    /// e.g. ["a", "b"] → "\"a\", \"b\""
    /// </summary>
    public static string FormatExpectedOptions(ReadOnlySpan<string> options)
    {
        if (options.Length == 0) return string.Empty;
        if (options.Length == 1) return $"\"{options[0]}\"";

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < options.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('"').Append(options[i]).Append('"');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Finds the closest match from a pre-formatted expected-keys string (e.g. <c>"\"a\", \"b\", \"c\""</c>).
    /// Parses the keys out and delegates to <see cref="FindClosest"/>.
    /// Used only on error paths — allocation is acceptable.
    /// </summary>
    public static string? FindClosestFromFormattedKeys(string input, string formattedKeys)
    {
        var candidates = ParseFormattedKeys(formattedKeys);
        return FindClosest(input, candidates);
    }

    /// <summary>
    /// Parses a pre-formatted expected-keys string like <c>"\"a\", \"b\""</c> into a string array <c>["a", "b"]</c>.
    /// </summary>
    private static string[] ParseFormattedKeys(string formatted)
    {
        if (string.IsNullOrEmpty(formatted)) return [];

        var keys = new System.Collections.Generic.List<string>();
        var i = 0;
        while (i < formatted.Length)
        {
            var start = formatted.IndexOf('"', i);
            if (start < 0) break;
            var end = formatted.IndexOf('"', start + 1);
            if (end < 0) break;
            keys.Add(formatted.Substring(start + 1, end - start - 1));
            i = end + 1;
        }
        return keys.ToArray();
    }
}
