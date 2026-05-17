namespace Seiton.Update.Services;

internal static class UnpinnedToolsSourcePathResolver
{
    public static string ResolvePrimaryDir(string repoRoot) =>
        Path.Combine(repoRoot, "data", "sources", "unpinned-tools");

    public static string ResolvePrimary(string repoRoot)
    {
        var path = Path.Combine(ResolvePrimaryDir(repoRoot), "unpinned_tools.json");
        if (File.Exists(path))
        {
            return path;
        }

        throw new FileNotFoundException(
            "Primary unpinned-tools source not found. Create data/sources/unpinned-tools/unpinned_tools.json.",
            path);
    }
}
