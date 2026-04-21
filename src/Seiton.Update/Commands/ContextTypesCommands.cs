using Seiton.Update.Services;
using Seiton.Update.Sources;
using Seiton.Update.Validators;

namespace Seiton.Update.Commands;

internal static class ContextTypesCommands
{
    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new GitHubContextTypesFetcher();
        var entry = await fetcher.FetchAsync(repoRoot);

        var manifestService = new WebhookManifestService();
        var manifest = manifestService.Load(repoRoot);
        manifest = manifestService.Upsert(manifest, entry);
        manifestService.Save(repoRoot, manifest);

        UpdateLogger.Info("[fetch:context-types] manifest updated.");
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new GitHubContextTypesFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new GitHubContextTypesFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int Validate(string repoRoot)
    {
        var validator = new ContextTypesValidator();
        var result = validator.Validate(repoRoot);

        if (!result.HasFindings)
        {
            UpdateLogger.Info("[validate:context-types] all docs contexts and properties are registered in context-types.json.");
            return 0;
        }

        foreach (var ctx in result.NewContexts)
        {
            UpdateLogger.Warn($"[validate:context-types] new context in docs not in context-types.json: {ctx}");
        }

        foreach (var prop in result.NewProperties)
        {
            UpdateLogger.Warn($"[validate:context-types] new property in docs: {prop.ContextName}.{prop.DotPath} ({prop.Type})");
        }

        UpdateLogger.Warn($"[validate:context-types] {result.NewContexts.Count} new context(s), {result.NewProperties.Count} new property path(s) found in GitHub Docs.");
        return 0;
    }

    public static int MergeSources(string repoRoot)
    {
        var mergeService = new ContextTypesMergeService();
        var changed = mergeService.Merge(repoRoot);

        UpdateLogger.Info(changed
            ? "[merge:context-types:sources] regenerated context-types.json from docs + override"
            : "[merge:context-types:sources] context-types.json is already up to date");

        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var syncService = new ContextTypesSyncService();
        var changed = syncService.Sync(repoRoot);

        UpdateLogger.Info(changed
            ? "[sync:context-types] regenerated src/Seiton.Core/Generated/ContextTypes.g.cs"
            : "[sync:context-types] no file changes in ContextTypes.g.cs");

        // Auto-validate against docs when parsed data is available
        var parsedPath = GitHubContextTypesFetcher.Paths(repoRoot).ParsedDocsPath;
        if (File.Exists(parsedPath))
        {
            var validator = new ContextTypesValidator();
            var result = validator.Validate(repoRoot);
            if (result.HasFindings)
            {
                foreach (var ctx in result.NewContexts)
                {
                    UpdateLogger.Warn($"[sync:context-types] new context in docs not in context-types.json: {ctx}");
                }

                foreach (var prop in result.NewProperties)
                {
                    UpdateLogger.Warn($"[sync:context-types] new property in docs: {prop.ContextName}.{prop.DotPath} ({prop.Type})");
                }

                UpdateLogger.Warn($"[sync:context-types] {result.NewContexts.Count} new context(s), {result.NewProperties.Count} new property path(s) found in GitHub Docs. Run merge-context-types-sources to update.");
            }
        }

        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new ContextTypesSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:context-types] generated file is stale against source. run sync first.");
            return 1;
        }

        UpdateLogger.Info("[verify:context-types] generated file is up to date.");
        return 0;
    }
}
