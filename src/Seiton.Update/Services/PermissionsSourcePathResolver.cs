namespace Seiton.Update.Services;

internal static class PermissionsSourcePathResolver
{
    public static string ResolvePrimary(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "data", "sources", "permissions", "permissions.json");
        if (File.Exists(path))
        {
            return path;
        }

        throw new FileNotFoundException(
            "Primary permissions source not found. Provide data/sources/permissions/permissions.json.",
            path);
    }
}
