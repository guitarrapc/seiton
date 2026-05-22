using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Commands;

internal static class SuperfluousActionsCommands
{
    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new GitHubSuperfluousActionsFetcher();
        var entry = await fetcher.FetchAsync(repoRoot);

        var manifestService = new ManifestService();
        var manifest = manifestService.Load(repoRoot);
        manifest = manifestService.Upsert(manifest, entry);
        manifestService.Save(repoRoot, manifest);

        UpdateLogger.Info("[fetch:superfluous-actions] manifest updated.");
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new GitHubSuperfluousActionsFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new GitHubSuperfluousActionsFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int MergeSources(string repoRoot)
    {
        var fetcher = new GitHubSuperfluousActionsFetcher();
        fetcher.MergeParsedSources(repoRoot);
        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var syncService = new SuperfluousActionsSyncService();
        var changed = syncService.Sync(repoRoot);
        UpdateLogger.Info(changed
            ? "[sync:superfluous-actions] regenerated src/Seiton.Core/Generated/SuperfluousActions.g.cs"
            : "[sync:superfluous-actions] no file changes in SuperfluousActions.g.cs");
        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new SuperfluousActionsSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:superfluous-actions] generated file is stale. run: dotnet run --project src/Seiton.Update -- sync-superfluous-actions");
            return 4;
        }

        UpdateLogger.Info("[verify:superfluous-actions] generated file is up to date.");
        return 0;
    }
}
