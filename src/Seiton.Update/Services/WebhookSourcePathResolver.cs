namespace Seiton.Update.Services;

internal static class WebhookSourcePathResolver
{
    public static string ResolvePrimary(string repoRoot)
    {
        var githubSnapshot = Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "webhook_types.json");
        if (File.Exists(githubSnapshot))
        {
            return githubSnapshot;
        }

        var legacySnapshot = Path.Combine(repoRoot, "data", "sources", "webhooks", "webhook_types.json");
        if (File.Exists(legacySnapshot))
        {
            return legacySnapshot;
        }

        throw new FileNotFoundException(
            "Primary webhook source not found. Provide data/sources/webhooks/github/webhook_types.json.",
            githubSnapshot);
    }
}
