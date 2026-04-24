namespace Seiton.Update.Services;

internal static class PermissionsSourcePathResolver
{
    public static string ResolvePrimary(string repoRoot)
    {
        var githubSnapshot = Path.Combine(repoRoot, "data", "sources", "permissions", "github", "permissions.json");
        if (File.Exists(githubSnapshot))
        {
            return githubSnapshot;
        }

        var legacySnapshot = Path.Combine(repoRoot, "data", "sources", "permissions", "permissions.json");
        if (File.Exists(legacySnapshot))
        {
            return legacySnapshot;
        }

        throw new FileNotFoundException(
            "Primary permissions source not found. Provide data/sources/permissions/github/permissions.json.",
            githubSnapshot);
    }
}
