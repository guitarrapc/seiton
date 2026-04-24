namespace Seiton.Update.Services;

internal static class IanaTimeZonesSourcePathResolver
{
    public static string ResolvePrimary(string repoRoot)
    {
        var ianaSnapshot = Path.Combine(repoRoot, "data", "sources", "iana-timezones", "iana", "iana_timezones.json");
        if (File.Exists(ianaSnapshot))
        {
            return ianaSnapshot;
        }

        throw new FileNotFoundException(
            "Primary IANA timezones source not found. Provide data/sources/iana-timezones/iana/iana_timezones.json.",
            ianaSnapshot);
    }
}
