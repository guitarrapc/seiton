using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Commands;

internal static class PopularActionsCommands
{
    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new GitHubPopularActionsFetcher();
        var entry = await fetcher.FetchAsync(repoRoot);

        var manifestService = new WebhookManifestService();
        var manifest = manifestService.Load(repoRoot);
        manifest = manifestService.Upsert(manifest, entry);
        manifestService.Save(repoRoot, manifest);

        UpdateLogger.Info("[fetch:popular-actions] manifest updated.");
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new GitHubPopularActionsFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new GitHubPopularActionsFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int MergeSources(string repoRoot)
    {
        var fetcher = new GitHubPopularActionsFetcher();
        fetcher.MergeParsedSources(repoRoot);
        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var syncService = new PopularActionsSyncService();
        var changed = syncService.Sync(repoRoot);

        UpdateLogger.Info(changed
            ? "[sync:popular-actions] regenerated src/Seiton.Core/Generated/PopularActions.g.cs"
            : "[sync:popular-actions] no file changes in PopularActions.g.cs");

        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new PopularActionsSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:popular-actions] generated file is stale against GitHub primary source. run sync first.");
            return 4;
        }

        UpdateLogger.Info("[verify:popular-actions] generated file is up to date with GitHub primary source.");
        return 0;
    }
}
