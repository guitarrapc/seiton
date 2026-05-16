using Seiton.Core.Linting;

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
        var maxDistance = GetThreshold(input.Length);
        var maxCandidateLength = GetMaxCandidateLength(candidates);
        if (input.Length > maxCandidateLength + maxDistance)
        {
            return null;
        }

        string? best = null;
        var bestDistance = maxDistance + 1;

        foreach (var candidate in candidates)
        {
            var distance = EditDistance.ComputeIgnoreCase(input, candidate, maxDistance);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Finds the closest match from <paramref name="candidates"/> for the given <paramref name="input"/>.
    /// Enumerates the provided collection to find the nearest acceptable candidate.
    /// </summary>
    public static string? FindClosest(string input, IReadOnlyCollection<string> candidates)
    {
        var maxDistance = GetThreshold(input.Length);
        var maxCandidateLength = GetMaxCandidateLength(candidates);

        if (input.Length > maxCandidateLength + maxDistance)
        {
            return null;
        }

        string? best = null;
        var bestDistance = maxDistance + 1;

        foreach (var candidate in candidates)
        {
            var distance = EditDistance.ComputeIgnoreCase(input, candidate, maxDistance);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private static int GetThreshold(int inputLength) => inputLength switch
    {
        <= 4 => 1,
        <= 8 => 2,
        _ => 3,
    };

    private static int GetMaxCandidateLength(ReadOnlySpan<string> candidates)
    {
        var max = 0;
        for (var i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].Length > max)
            {
                max = candidates[i].Length;
            }
        }

        return max;
    }

    private static int GetMaxCandidateLength(IReadOnlyCollection<string> candidates)
    {
        var max = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Length > max)
            {
                max = candidate.Length;
            }
        }

        return max;
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
