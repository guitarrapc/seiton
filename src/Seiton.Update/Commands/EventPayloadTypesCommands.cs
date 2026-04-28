using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Commands;

internal static class EventPayloadTypesCommands
{
    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new GitHubEventPayloadTypesFetcher();
        var manifestEntry = await fetcher.FetchAsync(repoRoot);
        var manifestService = new ManifestService();
        var manifest = manifestService.Load(repoRoot);
        manifest = manifestService.Upsert(manifest, manifestEntry);
        manifestService.Save(repoRoot, manifest);
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new GitHubEventPayloadTypesFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new GitHubEventPayloadTypesFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var service = new EventPayloadTypesSyncService();
        var changed = service.Sync(repoRoot);
        UpdateLogger.Info(changed
            ? "[sync:event-payload-types] regenerated src/Seiton.Core/Generated/EventPayloadTypes.g.cs"
            : "[sync:event-payload-types] no file changes in EventPayloadTypes.g.cs");
        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var service = new EventPayloadTypesSyncService();
        if (!service.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:event-payload-types] generated file is stale. run: dotnet run --project src/Seiton.Update -- sync-event-payload-types");
            return 4;
        }

        UpdateLogger.Info("[verify:event-payload-types] generated file is up to date.");
        return 0;
    }
}
