namespace Seiton.Update.Services;

internal static class FunctionSpecsSourcePathResolver
{
    public static string ResolvePrimary(string repoRoot)
    {
        var githubSnapshot = Path.Combine(repoRoot, "data", "sources", "function-specs", "github", "function-specs.json");
        if (File.Exists(githubSnapshot))
        {
            return githubSnapshot;
        }

        var legacySnapshot = Path.Combine(repoRoot, "data", "sources", "function-specs", "function-specs.json");
        if (File.Exists(legacySnapshot))
        {
            return legacySnapshot;
        }

        throw new FileNotFoundException(
            "Primary function-specs source not found. Provide data/sources/function-specs/github/function-specs.json.",
            githubSnapshot);
    }
}
