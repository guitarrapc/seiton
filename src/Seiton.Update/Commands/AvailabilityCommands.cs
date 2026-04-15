using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Commands;

internal static class AvailabilityCommands
{
    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new GitHubAvailabilityFetcher();
        var entry = await fetcher.FetchAsync(repoRoot);

        var manifestService = new WebhookManifestService();
        var manifest = manifestService.Load(repoRoot);
        manifest = manifestService.Upsert(manifest, entry);
        manifestService.Save(repoRoot, manifest);

        UpdateLogger.Info("[fetch:availability] manifest updated.");
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new GitHubAvailabilityFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new GitHubAvailabilityFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int MergeSources(string repoRoot)
    {
        var fetcher = new GitHubAvailabilityFetcher();
        fetcher.MergeParsedSources(repoRoot);
        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var syncService = new AvailabilitySyncService();
        var changed = syncService.Sync(repoRoot);

        UpdateLogger.Info(changed
            ? "[sync:availability] regenerated src/Seiton.Core/Generated/Availability.g.cs"
            : "[sync:availability] no file changes in Availability.g.cs");

        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new AvailabilitySyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:availability] generated file is stale against GitHub primary source. run sync first.");
            return 4;
        }

        UpdateLogger.Info("[verify:availability] generated file is up to date with GitHub primary source.");
        return 0;
    }
}
