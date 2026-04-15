namespace Seiton.Update.Services;

internal static class PopularActionsSourcePathResolver
{
    public static string ResolvePrimary(string repoRoot)
    {
        var githubSnapshot = Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "popular_actions.json");
        if (File.Exists(githubSnapshot))
        {
            return githubSnapshot;
        }

        var legacySnapshot = Path.Combine(repoRoot, "data", "sources", "popular-actions", "popular_actions.json");
        if (File.Exists(legacySnapshot))
        {
            return legacySnapshot;
        }

        throw new FileNotFoundException(
            "Primary popular-actions source not found. Provide data/sources/popular-actions/github/popular_actions.json.",
            githubSnapshot);
    }
}
