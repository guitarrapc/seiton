namespace Seiton.Cli;

internal static class CliOptionSuggester
{
    static readonly HashSet<string> KnownLongOptions =
    [
        "--help",
        "--version",
        "--config",
        "--stdin-filename",
        "--ignore",
        "--min-severity",
        "--format",
        "--oneline",
        "--color",
        "--no-color",
        "--verbose",
        "--fix",
        "--dry-run",
        "--check",
        "--enable-pin-network",
        "--enable-image-network",
        "--include-actions",
        "--output",
        "--force",
    ];

    const int SuggestionDistanceThreshold = 3;

    public static bool TryWriteSuggestionForUnknownOption(string[] args, TextWriter errorWriter)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (raw == "--")
            {
                break;
            }

            if (!TryGetLongOptionToken(raw, out var optionToken))
            {
                continue;
            }

            if (KnownLongOptions.Contains(optionToken))
            {
                continue;
            }

            var suggestion = FindBestSuggestion(optionToken);
            if (suggestion is null)
            {
                continue;
            }

            errorWriter.WriteLine($"Argument '{optionToken}' is not recognized.");
            errorWriter.WriteLine($"Did you mean '{suggestion}'?");
            return true;
        }

        return false;
    }

    static bool TryGetLongOptionToken(string raw, out string optionToken)
    {
        optionToken = string.Empty;
        if (!raw.StartsWith("--", StringComparison.Ordinal) || raw.Length <= 2)
        {
            return false;
        }

        var eqIndex = raw.IndexOf('=');
        optionToken = eqIndex > 2 ? raw[..eqIndex] : raw;
        return true;
    }

    static string? FindBestSuggestion(string optionToken)
    {
        var normalizedInput = Normalize(optionToken);
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var known in KnownLongOptions)
        {
            var normalizedKnown = Normalize(known);
            if (normalizedInput.Equals(normalizedKnown, StringComparison.Ordinal))
            {
                return known;
            }

            var distance = LevenshteinDistance(normalizedInput, normalizedKnown);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = known;
        }

        return bestDistance <= SuggestionDistanceThreshold ? best : null;
    }

    static string Normalize(string option)
    {
        return option.Replace("-", string.Empty, StringComparison.Ordinal);
    }

    static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitutionCost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + substitutionCost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }
}
