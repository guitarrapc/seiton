namespace Seiton.Update.Services;

internal static class RunnerLabelsSourcePathResolver
{
    public static string ResolvePrimary(string repoRoot)
    {
        var githubSnapshot = Path.Combine(repoRoot, "data", "sources", "runner-labels", "github", "runner_labels.json");
        if (File.Exists(githubSnapshot))
        {
            return githubSnapshot;
        }

        var legacySnapshot = Path.Combine(repoRoot, "data", "sources", "runner-labels", "runner_labels.json");
        if (File.Exists(legacySnapshot))
        {
            return legacySnapshot;
        }

        throw new FileNotFoundException(
            "Primary runner-labels source not found. Provide data/sources/runner-labels/github/runner_labels.json.",
            githubSnapshot);
    }
}
