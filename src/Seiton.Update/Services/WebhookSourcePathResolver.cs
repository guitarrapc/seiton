namespace Seiton.Update.Services;

internal static class WebhookSourcePathResolver
{
    public static string Resolve(string repoRoot)
    {
        var vendored = Path.Combine(repoRoot, "data", "sources", "webhooks", "all_webhooks.go");
        if (File.Exists(vendored))
        {
            return vendored;
        }

        var reference = Path.Combine(repoRoot, ".references", "actionlint", "all_webhooks.go");
        if (File.Exists(reference))
        {
            return reference;
        }

        throw new FileNotFoundException(
            "Webhook source not found. Provide data/sources/webhooks/all_webhooks.go or .references/actionlint/all_webhooks.go.",
            vendored);
    }
}
