using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Commands;

internal static class IanaTimeZonesCommands
{
    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new IanaTimeZonesFetcher();
        var entry = await fetcher.FetchAsync(repoRoot);

        var manifestService = new ManifestService();
        var manifest = manifestService.Load(repoRoot);
        manifest = manifestService.Upsert(manifest, entry);
        manifestService.Save(repoRoot, manifest);

        UpdateLogger.Info("[fetch:iana-timezones] manifest updated.");
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new IanaTimeZonesFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new IanaTimeZonesFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int MergeSources(string repoRoot)
    {
        var fetcher = new IanaTimeZonesFetcher();
        fetcher.MergeParsedSources(repoRoot);
        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var syncService = new IanaTimeZonesSyncService();
        var changed = syncService.Sync(repoRoot);

        UpdateLogger.Info(changed
            ? "[sync:iana-timezones] regenerated src/Seiton.Core/Generated/IanaTimeZones.g.cs"
            : "[sync:iana-timezones] no file changes in IanaTimeZones.g.cs");

        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new IanaTimeZonesSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:iana-timezones] generated file is stale against IANA primary source. run sync first.");
            return 4;
        }

        UpdateLogger.Info("[verify:iana-timezones] generated file is up to date with IANA primary source.");
        return 0;
    }
}
