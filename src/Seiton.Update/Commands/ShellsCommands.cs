using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Commands;

internal static class ShellsCommands
{
    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new GitHubShellsFetcher();
        var manifest = await fetcher.FetchAsync(repoRoot);

        var manifestService = new ManifestService();
        var manifestData = manifestService.Load(repoRoot);
        manifestData = manifestService.Upsert(manifestData, manifest);
        manifestService.Save(repoRoot, manifestData);

        UpdateLogger.Info($"[fetch:shells] completed. manifest updated. dataset={manifest.Dataset}");
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new GitHubShellsFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new GitHubShellsFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int MergeSources(string repoRoot)
    {
        var fetcher = new GitHubShellsFetcher();
        fetcher.MergeParsedSources(repoRoot);
        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var syncService = new ShellsSyncService();
        var changed = syncService.Sync(repoRoot);
        UpdateLogger.Info(changed
            ? "[sync:shells] regenerated src/Seiton.Core/Generated/Shells.g.cs"
            : "[sync:shells] no file changes in Shells.g.cs");
        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new ShellsSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:shells] generated file is stale. run: dotnet run --project src/Seiton.Update -- sync-shells");
            return 4;
        }

        UpdateLogger.Info("[verify:shells] generated file is up to date.");
        return 0;
    }
}
