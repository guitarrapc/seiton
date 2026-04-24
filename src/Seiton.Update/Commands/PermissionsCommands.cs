using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Commands;

internal static class PermissionsCommands
{
    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new GitHubPermissionsFetcher();
        var entry = await fetcher.FetchAsync(repoRoot);

        var manifestService = new WebhookManifestService();
        var manifest = manifestService.Load(repoRoot);
        manifest = manifestService.Upsert(manifest, entry);
        manifestService.Save(repoRoot, manifest);

        UpdateLogger.Info("[fetch:permissions] manifest updated.");
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new GitHubPermissionsFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new GitHubPermissionsFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int MergeSources(string repoRoot)
    {
        var fetcher = new GitHubPermissionsFetcher();
        fetcher.MergeParsedSources(repoRoot);
        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var syncService = new PermissionsSyncService();
        var changed = syncService.Sync(repoRoot);
        UpdateLogger.Info(changed
            ? "[sync:permissions] regenerated src/Seiton.Core/Generated/PermissionScopes.g.cs"
            : "[sync:permissions] no file changes in PermissionScopes.g.cs");
        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new PermissionsSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:permissions] generated file is stale. run: dotnet run --project src/Seiton.Update -- sync-permissions");
            return 4;
        }

        UpdateLogger.Info("[verify:permissions] generated file is up to date.");
        return 0;
    }
}
