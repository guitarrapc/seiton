using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Commands;

internal static class ExpectedKeysCommands
{
    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new GitHubExpectedKeysFetcher();
        var manifest = await fetcher.FetchAsync(repoRoot);

        var manifestService = new ManifestService();
        var manifestData = manifestService.Load(repoRoot);
        manifestData = manifestService.Upsert(manifestData, manifest);
        manifestService.Save(repoRoot, manifestData);

        UpdateLogger.Info($"[fetch:expected-keys] completed. manifest updated. dataset={manifest.Dataset}");
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new GitHubExpectedKeysFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new GitHubExpectedKeysFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int MergeSources(string repoRoot)
    {
        var fetcher = new GitHubExpectedKeysFetcher();
        fetcher.MergeParsedSources(repoRoot);
        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var syncService = new ExpectedKeysSyncService();
        var changed = syncService.Sync(repoRoot);
        UpdateLogger.Info(changed
            ? "[sync:expected-keys] regenerated src/Seiton.Core/Generated/ExpectedKeys.g.cs"
            : "[sync:expected-keys] no file changes in ExpectedKeys.g.cs");
        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new ExpectedKeysSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:expected-keys] generated file is stale. run: dotnet run --project src/Seiton.Update -- sync-expected-keys");
            return 4;
        }

        UpdateLogger.Info("[verify:expected-keys] generated file is up to date.");
        return 0;
    }
}
