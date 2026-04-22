using Seiton.Update.Services;
using Seiton.Update.Sources;
using Seiton.Update.Validators;

namespace Seiton.Update.Commands;

internal static class FunctionSpecsCommands
{
    public static async Task<int> Fetch(string repoRoot)
    {
        var fetcher = new GitHubFunctionNamesFetcher();
        var entry = await fetcher.FetchAsync(repoRoot);

        var manifestService = new WebhookManifestService();
        var manifest = manifestService.Load(repoRoot);
        manifest = manifestService.Upsert(manifest, entry);
        manifestService.Save(repoRoot, manifest);

        UpdateLogger.Info("[fetch:function-specs] manifest updated.");
        return 0;
    }

    public static async Task<int> FetchSources(string repoRoot)
    {
        var fetcher = new GitHubFunctionNamesFetcher();
        await fetcher.FetchSourceFilesAsync(repoRoot);
        return 0;
    }

    public static int ParseSources(string repoRoot)
    {
        var fetcher = new GitHubFunctionNamesFetcher();
        fetcher.ParseLocalSourceFiles(repoRoot);
        return 0;
    }

    public static int Validate(string repoRoot)
    {
        var validator = new FunctionSpecsValidator();
        var unregistered = validator.Validate(repoRoot);
        if (unregistered.Count > 0)
        {
            foreach (var name in unregistered)
            {
                UpdateLogger.Warn($"[validate:function-specs] unregistered function in docs: {name}");
            }

            UpdateLogger.Warn($"[validate:function-specs] {unregistered.Count} function(s) found in GitHub Docs but missing from function-specs.json.");
            return 0; // warning, not error
        }

        UpdateLogger.Info("[validate:function-specs] all docs functions are registered in function-specs.json.");
        return 0;
    }

    public static int Sync(string repoRoot)
    {
        var syncService = new FunctionSpecsSyncService();
        var changed = syncService.Sync(repoRoot);

        UpdateLogger.Info(changed
            ? "[sync:function-specs] regenerated src/Seiton.Core/Generated/FunctionSpecs.g.cs"
            : "[sync:function-specs] no file changes in FunctionSpecs.g.cs");

        // Run validation if parsed function names are available
        var parsedPath = Path.Combine(repoRoot, "data", "sources", "function-specs", "github", "parsed", "docs-function-names.json");
        if (File.Exists(parsedPath))
        {
            var validator = new FunctionSpecsValidator();
            var unregistered = validator.Validate(repoRoot);
            if (unregistered.Count > 0)
            {
                foreach (var name in unregistered)
                {
                    UpdateLogger.Warn($"[sync:function-specs] unregistered function in docs: {name}");
                }

                UpdateLogger.Warn($"[sync:function-specs] {unregistered.Count} function(s) found in GitHub Docs but missing from function-specs.json.");
            }
        }

        return 0;
    }

    public static int Verify(string repoRoot)
    {
        var syncService = new FunctionSpecsSyncService();
        if (!syncService.IsUpToDate(repoRoot))
        {
            UpdateLogger.Error("[verify:function-specs] generated file is stale against source. run sync first.");
            return 1;
        }

        UpdateLogger.Info("[verify:function-specs] generated file is up to date.");
        return 0;
    }
}
