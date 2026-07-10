namespace Seiton.Update.Services;

internal static class StepSchemaSourcePathResolver
{
    public static string ResolveGithubDir(string repoRoot) =>
        Path.Combine(repoRoot, "data", "sources", "step-schema", "github");

    public static string ResolveRawDir(string repoRoot) =>
        Path.Combine(ResolveGithubDir(repoRoot), "raw");

    public static string ResolveParsedDir(string repoRoot) =>
        Path.Combine(ResolveGithubDir(repoRoot), "parsed");

    public static string ResolveParsed(string repoRoot)
    {
        var path = Path.Combine(ResolveParsedDir(repoRoot), "step-schema.json");
        if (File.Exists(path))
        {
            return path;
        }

        throw new FileNotFoundException(
            "Step schema parsed source is missing. Run parse-step-schema-sources first.",
            path);
    }

    public static string ResolveSupplemental(string repoRoot) =>
        Path.Combine(ResolveGithubDir(repoRoot), "supplemental-step-schema.json");

    public static string ResolvePrimary(string repoRoot)
    {
        var path = Path.Combine(ResolveGithubDir(repoRoot), "step-schema.json");
        if (File.Exists(path))
        {
            return path;
        }

        throw new FileNotFoundException(
            "Primary step-schema source not found. Run merge-step-schema-sources after parse (or fetch-step-schema).",
            path);
    }
}
