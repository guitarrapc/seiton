namespace Seiton.Update.Services;

internal static class SuperfluousActionsSourcePathResolver
{
    public static string ResolvePrimaryDir(string repoRoot) =>
        Path.Combine(repoRoot, "data", "sources", "superfluous-actions", "github");

    public static string ResolvePrimary(string repoRoot)
    {
        var path = Path.Combine(ResolvePrimaryDir(repoRoot), "superfluous_actions.json");
        if (File.Exists(path))
        {
            return path;
        }

        throw new FileNotFoundException(
            "Primary superfluous-actions source not found. Run fetch-superfluous-actions first.",
            path);
    }
}
