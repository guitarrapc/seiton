namespace Seiton.Update.Services;

internal static class ContextTypesSourcePathResolver
{
    public static string ResolvePrimary(string repoRoot)
    {
        var githubSnapshot = Path.Combine(repoRoot, "data", "sources", "context-types", "github", "context-types.json");
        if (File.Exists(githubSnapshot))
        {
            return githubSnapshot;
        }

        var legacySnapshot = Path.Combine(repoRoot, "data", "sources", "context-types", "context-types.json");
        if (File.Exists(legacySnapshot))
        {
            return legacySnapshot;
        }

        throw new FileNotFoundException(
            "Primary context-types source not found. Provide data/sources/context-types/github/context-types.json.",
            githubSnapshot);
    }
}
