namespace Seiton.Update.Services;

internal static class AvailabilitySourcePathResolver
{
    public static string ResolvePrimary(string repoRoot)
    {
        var githubSnapshot = Path.Combine(repoRoot, "data", "sources", "availability", "github", "availability.json");
        if (File.Exists(githubSnapshot))
        {
            return githubSnapshot;
        }

        var legacySnapshot = Path.Combine(repoRoot, "data", "sources", "availability", "availability.json");
        if (File.Exists(legacySnapshot))
        {
            return legacySnapshot;
        }

        throw new FileNotFoundException(
            "Primary availability source not found. Provide data/sources/availability/github/availability.json.",
            githubSnapshot);
    }
}
