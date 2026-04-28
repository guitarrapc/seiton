namespace Seiton.Update.Services;

internal static class ShellsSourcePathResolver
{
    public static string ResolvePrimary(string repoRoot)
    {
        var githubSnapshot = Path.Combine(repoRoot, "data", "sources", "shells", "github", "shells.json");
        if (File.Exists(githubSnapshot))
        {
            return githubSnapshot;
        }

        throw new FileNotFoundException(
            "Primary shells source not found. Provide data/sources/shells/github/shells.json.",
            githubSnapshot);
    }
}
