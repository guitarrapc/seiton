using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Commands;

internal static class StepSchemaCommands
{
    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new GitHubStepSchemaFetcher();
        var manifest = await fetcher.FetchAsync(repoRoot);

        var manifestService = new ManifestService();
        var manifestData = manifestService.Load(repoRoot);
        manifestData = manifestService.Upsert(manifestData, manifest);
        manifestService.Save(repoRoot, manifestData);

        UpdateLogger.Info($"[fetch:step-schema] completed. manifest updated. dataset={manifest.Dataset}");
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new GitHubStepSchemaFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new GitHubStepSchemaFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int MergeSources(string repoRoot)
    {
        var fetcher = new GitHubStepSchemaFetcher();
        fetcher.MergeParsedSources(repoRoot);
        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var syncService = new StepSchemaSyncService();
        var changed = syncService.Sync(repoRoot);
        UpdateLogger.Info(changed
            ? "[sync:step-schema] regenerated src/Seiton.Core/Generated/StepSchema.g.cs"
            : "[sync:step-schema] no file changes in StepSchema.g.cs");
        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new StepSchemaSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:step-schema] generated file is stale. run: dotnet run --project src/Seiton.Update -- sync-step-schema");
            return 4;
        }

        UpdateLogger.Info("[verify:step-schema] generated file is up to date.");
        return 0;
    }
}
