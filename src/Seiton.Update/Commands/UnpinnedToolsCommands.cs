using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Commands;

internal static class UnpinnedToolsCommands
{
    public static int ValidateTargets(string repoRoot)
    {
        var fetcher = new GitHubUnpinnedToolsFetcher();
        fetcher.ValidateTargetsConfig(repoRoot);
        UpdateLogger.Info("[validate:unpinned-tools:targets] targets.json is valid.");
        return 0;
    }

    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new GitHubUnpinnedToolsFetcher();
        var entry = await fetcher.FetchAsync(repoRoot);

        var manifestService = new ManifestService();
        var manifest = manifestService.Load(repoRoot);
        manifest = manifestService.Upsert(manifest, entry);
        manifestService.Save(repoRoot, manifest);

        UpdateLogger.Info("[fetch:unpinned-tools] manifest updated.");
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new GitHubUnpinnedToolsFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new GitHubUnpinnedToolsFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int MergeSources(string repoRoot)
    {
        var fetcher = new GitHubUnpinnedToolsFetcher();
        fetcher.MergeParsedSources(repoRoot);
        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var syncService = new UnpinnedToolsSyncService();
        var changed = syncService.Sync(repoRoot);
        UpdateLogger.Info(changed
            ? "[sync:unpinned-tools] regenerated src/Seiton.Core/Generated/UnpinnedToolsActions.g.cs"
            : "[sync:unpinned-tools] no file changes in UnpinnedToolsActions.g.cs");
        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new UnpinnedToolsSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:unpinned-tools] generated file is stale. run: dotnet run --project src/Seiton.Update -- sync-unpinned-tools");
            return 4;
        }

        UpdateLogger.Info("[verify:unpinned-tools] generated file is up to date.");
        return 0;
    }
}
