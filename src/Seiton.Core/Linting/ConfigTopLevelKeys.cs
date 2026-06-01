namespace Seiton.Core.Linting;
/// <summary>Known top-level lint-config keys and typo suggestions for unknown keys.</summary>
internal static class ConfigTopLevelKeys
{
    private static readonly string[] KnownKeys =
    [
        "rules",
        "exclusions",
        "fix",
        "network",
        "output",
        "discovery",
    ];

    /// <summary>Builds an error message for an unknown top-level key, with a suggestion when a close match exists.</summary>
    public static string BuildUnknownKeyMessage(string key)
    {
        var suggested = SuggestTopLevelKey(key);
        return suggested is null
            ? $"unknown top-level key '{key}'"
            : $"unknown top-level key '{key}'. Did you mean '{suggested}'?";
    }

    private static string? SuggestTopLevelKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        const int maxDistance = 5;
        string? best = null;
        var bestDistance = maxDistance + 1;

        for (var i = 0; i < KnownKeys.Length; i++)
        {
            var distance = EditDistance.ComputeIgnoreCase(key, KnownKeys[i], maxDistance);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = KnownKeys[i];
        }

        return bestDistance <= maxDistance ? best : null;
    }
}
