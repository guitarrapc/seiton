namespace Seiton.Update.Services;

internal static class UnpinnedToolsSourcePathResolver
{
    public static string ResolvePrimaryDir(string repoRoot) =>
        Path.Combine(repoRoot, "data", "sources", "unpinned-tools");

    public static string ResolvePrimary(string repoRoot)
    {
        var githubSnapshot = Path.Combine(repoRoot, "data", "sources", "unpinned-tools", "github", "unpinned_tools.json");
        if (File.Exists(githubSnapshot))
        {
            return githubSnapshot;
        }

        var legacyPath = Path.Combine(ResolvePrimaryDir(repoRoot), "unpinned_tools.json");
        if (File.Exists(legacyPath))
        {
            return legacyPath;
        }

        throw new FileNotFoundException(
            "Primary unpinned-tools source not found. Run fetch-unpinned-tools first, or provide data/sources/unpinned-tools/github/unpinned_tools.json.",
            githubSnapshot);
    }
}
