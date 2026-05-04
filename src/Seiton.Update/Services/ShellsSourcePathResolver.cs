namespace Seiton.Update.Services;

internal static class ShellsSourcePathResolver
{
    public static string ResolveRawDir(string repoRoot) =>
        Path.Combine(repoRoot, "data", "sources", "shells", "github", "raw");

    public static string ResolveParsedDir(string repoRoot) =>
        Path.Combine(repoRoot, "data", "sources", "shells", "github", "parsed");

    public static string ResolvePrimaryDir(string repoRoot) =>
        Path.Combine(repoRoot, "data", "sources", "shells", "github");

    public static string ResolvePrimary(string repoRoot)
    {
        var path = Path.Combine(ResolvePrimaryDir(repoRoot), "shells.json");
        if (File.Exists(path))
        {
            return path;
        }

        throw new FileNotFoundException(
            "Primary shells source not found. Run merge-shells-sources after parse (or fetch-shells).",
            path);
    }
}
