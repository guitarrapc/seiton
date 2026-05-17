namespace Seiton.Update.Services;

internal static class BotActorsSourcePathResolver
{
    public static string ResolvePrimaryDir(string repoRoot) =>
        Path.Combine(repoRoot, "data", "sources", "bot-actors", "github");

    public static string ResolvePrimary(string repoRoot)
    {
        var path = Path.Combine(ResolvePrimaryDir(repoRoot), "bot-actors.json");
        if (File.Exists(path))
        {
            return path;
        }

        throw new FileNotFoundException(
            "Primary bot-actors source not found.",
            path);
    }
}
