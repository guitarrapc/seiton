namespace Seiton.Update.Services;

internal static class ExpectedKeysSourcePathResolver
{
    public static string ResolveRaw(string repoRoot)
    {
        var rawPath = Path.Combine(repoRoot, "data", "sources", "expected-keys", "github", "raw", "workflow-syntax.md");
        if (File.Exists(rawPath))
        {
            return rawPath;
        }

        throw new FileNotFoundException(
            "Expected keys raw source not found. Run fetch-expected-keys-sources first.",
            rawPath);
    }

    public static string ResolveRawDir(string repoRoot)
    {
        return Path.Combine(repoRoot, "data", "sources", "expected-keys", "github", "raw");
    }

    public static string ResolvePrimary(string repoRoot)
    {
        var githubSnapshot = Path.Combine(repoRoot, "data", "sources", "expected-keys", "github", "expected-keys.json");
        if (File.Exists(githubSnapshot))
        {
            return githubSnapshot;
        }

        throw new FileNotFoundException(
            "Primary expected-keys source not found. Run parse-expected-keys-sources first.",
            githubSnapshot);
    }

    public static string ResolvePrimaryDir(string repoRoot)
    {
        return Path.Combine(repoRoot, "data", "sources", "expected-keys", "github");
    }
}
