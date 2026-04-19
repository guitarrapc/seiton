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

    static readonly HashSet<string> LongOptionsWithValue =
    [
        "--config",
        "--stdin-filename",
        "--ignore",
        "--min-severity",
        "--format",
        "--color",
        "--output",
    ];

    const int SuggestionDistanceThreshold = 3;

    public static bool TryWriteSuggestionsForUnknownOptions(string[] args, TextWriter errorWriter)
    {
        var suggestions = CollectUnknownLongOptionSuggestions(args);
        if (suggestions.Count == 0)
        {
            return false;
        }

        var hasCandidateSuggestion = false;
        foreach (var item in suggestions)
        {
            errorWriter.WriteLine($"Argument '{item.OptionToken}' is not recognized.");

            if (item.Suggestion is not null)
            {
                hasCandidateSuggestion = true;
                errorWriter.WriteLine($"Did you mean '{item.Suggestion}'?");
            }
        }

        if (hasCandidateSuggestion)
        {
            var suggestedCommand = BuildSuggestedCommand(args);
            if (!string.IsNullOrWhiteSpace(suggestedCommand))
            {
                errorWriter.WriteLine($"Try: {suggestedCommand}");
            }
        }

        return true;
    }

    static List<OptionSuggestion> CollectUnknownLongOptionSuggestions(string[] args)
    {
        var unknownOptions = new List<OptionSuggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

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

            if (!seen.Add(optionToken))
            {
                continue;
            }

            var suggestion = FindBestSuggestion(optionToken);
            unknownOptions.Add(new OptionSuggestion(optionToken, suggestion));
        }

        return unknownOptions;
    }

    static string BuildSuggestedCommand(string[] args)
    {
        var tokens = new List<string>(args.Length + 1)
        {
            "seiton"
        };

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (raw == "--")
            {
                tokens.Add(raw);
                for (var rest = i + 1; rest < args.Length; rest++)
                {
                    tokens.Add(args[rest]);
                }

                break;
            }

            if (!TryGetLongOptionToken(raw, out var optionToken))
            {
                tokens.Add(raw);
                continue;
            }

            var replacement = KnownLongOptions.Contains(optionToken)
                ? optionToken
                : FindBestSuggestion(optionToken);

            if (replacement is null)
            {
                continue;
            }

            var eqIndex = raw.IndexOf('=');
            if (eqIndex > 2)
            {
                tokens.Add($"{replacement}{raw[eqIndex..]}");
                continue;
            }

            tokens.Add(replacement);
            if (!LongOptionsWithValue.Contains(replacement))
            {
                continue;
            }

            if (i + 1 >= args.Length)
            {
                continue;
            }

            var next = args[i + 1];
            if (next == "--")
            {
                continue;
            }

            if (next.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            tokens.Add(next);
            i++;
        }

        return string.Join(' ', tokens);
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

        return IsDistanceAcceptable(normalizedInput.Length, bestDistance) ? best : null;
    }

    static bool IsDistanceAcceptable(int inputLength, int distance)
    {
        var threshold = inputLength switch
        {
            <= 4 => 1,
            <= 8 => 2,
            _ => SuggestionDistanceThreshold,
        };

        return distance <= threshold;
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

    readonly record struct OptionSuggestion(string OptionToken, string? Suggestion);
}
