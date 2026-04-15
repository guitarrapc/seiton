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

    public static bool TryResolveActionlintReference(string repoRoot, out string referencePath)
    {
        var vendoredReference = Path.Combine(repoRoot, "data", "sources", "webhooks", "actionlint", "all_webhooks.go");
        if (File.Exists(vendoredReference))
        {
            referencePath = vendoredReference;
            return true;
        }

        var localReference = Path.Combine(repoRoot, ".references", "actionlint", "all_webhooks.go");
        if (File.Exists(localReference))
        {
            referencePath = localReference;
            return true;
        }

        referencePath = string.Empty;
        return false;
    }
}
