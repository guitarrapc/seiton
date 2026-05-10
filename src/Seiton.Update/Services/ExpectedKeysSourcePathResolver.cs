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

    public static string ResolveParsedDir(string repoRoot) =>
        Path.Combine(repoRoot, "data", "sources", "expected-keys", "github", "parsed");

    /// <summary>
    /// Stage 2 output: parsed key hierarchy from <c>raw/workflow-syntax.md</c>.
    /// </summary>
    public static string ResolveParsed(string repoRoot)
    {
        var path = Path.Combine(ResolveParsedDir(repoRoot), "expected-keys.json");
        if (File.Exists(path))
        {
            return path;
        }

        throw new FileNotFoundException(
            "Expected keys parsed source is missing. Run parse-expected-keys-sources first.",
            path);
    }

    /// <summary>
    /// Canonical snapshot for codegen (Stage 3 / merge output).
    /// </summary>
    public static string ResolvePrimary(string repoRoot)
    {
        var githubSnapshot = Path.Combine(repoRoot, "data", "sources", "expected-keys", "github", "expected-keys.json");
        if (File.Exists(githubSnapshot))
        {
            return githubSnapshot;
        }

        throw new FileNotFoundException(
            "Primary expected-keys source not found. Run merge-expected-keys-sources after parse (or fetch-expected-keys).",
            githubSnapshot);
    }

    public static string ResolvePrimaryDir(string repoRoot)
    {
        return Path.Combine(repoRoot, "data", "sources", "expected-keys", "github");
    }

    /// <summary>
    /// Hand-written supplemental sections (e.g. action-metadata keys not in workflow-syntax.md).
    /// </summary>
    public static string ResolveSupplementalKeys(string repoRoot)
    {
        return Path.Combine(repoRoot, "data", "sources", "expected-keys", "github", "supplemental-keys.json");
    }
}
