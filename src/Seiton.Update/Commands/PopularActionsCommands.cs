using Seiton.Update.Services;
using Seiton.Update.Sources;
using Seiton.Update.Validators;

namespace Seiton.Update.Commands;

internal static class PopularActionsCommands
{
    public static int ValidateTargets(string repoRoot)
    {
        var fetcher = new GitHubPopularActionsFetcher();
        fetcher.ValidateTargetsConfig(repoRoot);
        UpdateLogger.Info("[validate:popular-actions:targets] targets.json is valid.");
        return 0;
    }

    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new GitHubPopularActionsFetcher();
        var entry = await fetcher.FetchAsync(repoRoot);

        var manifestService = new ManifestService();
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

    public static async Task<int> ValidateVersions(string repoRoot)
    {
        var validator = new PopularActionsVersionValidator();
        var result = await validator.ValidateAsync(repoRoot);

        if (result.HasFindings)
        {
            foreach (var stale in result.StaleVersions)
            {
                UpdateLogger.Warn($"[validate:popular-actions:versions] {stale.ActionRef} is stale (current: v{stale.CurrentMajor}, latest: v{stale.LatestMajor})");
            }

            foreach (var unresolved in result.UnresolvedVersions)
            {
                UpdateLogger.Warn($"[validate:popular-actions:versions] {unresolved} could not be resolved against GitHub tags");
            }

            UpdateLogger.Error($"[validate:popular-actions:versions] {result.StaleVersions.Count} action(s) have newer major versions available; {result.UnresolvedVersions.Count} action(s) could not be resolved.");
            return 4;
        }

        UpdateLogger.Info("[validate:popular-actions:versions] all targets are up to date.");
        return 0;
    }
}
