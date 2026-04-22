namespace Seiton.Update.Services;

internal static class ContextTypesOverridePathResolver
{
    public static string Resolve(string repoRoot) =>
        Path.Combine(repoRoot, "data", "sources", "context-types", "github", "context-types-override.json");
}
