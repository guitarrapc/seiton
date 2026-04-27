using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Commands;

internal static class WebhookCommands
{
    public static async Task<int> Fetch(string repoRoot, bool excludeSchemaOnly = false)
    {
        var fetcher = new GitHubWebhookFetcher();
        var entry = await fetcher.FetchAsync(repoRoot, excludeSchemaOnly);

        var manifestService = new ManifestService();
        var manifest = manifestService.Load(repoRoot);
        manifest = manifestService.Upsert(manifest, entry);
        manifestService.Save(repoRoot, manifest);

        UpdateLogger.Info($"[fetch:webhooks] manifest updated. excludeSchemaOnly={excludeSchemaOnly}");
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new GitHubWebhookFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new GitHubWebhookFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int MergeSources(string repoRoot, bool excludeSchemaOnly = false)
    {
        var fetcher = new GitHubWebhookFetcher();
        fetcher.MergeParsedSources(repoRoot, excludeSchemaOnly);
        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var syncService = new WebhookSyncService();
        var changed = syncService.Sync(repoRoot);

        UpdateLogger.Info(changed
            ? "[sync:webhooks] regenerated src/Seiton.Core/Generated/WebhookTypes.g.cs"
            : "[sync:webhooks] no file changes in WebhookTypes.g.cs");

        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new WebhookSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:webhooks] generated file is stale against GitHub primary source. run sync first.");
            return 4;
        }

        UpdateLogger.Info("[verify:webhooks] generated file is up to date with GitHub primary source.");
        return 0;
    }
}
