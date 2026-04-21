using System.Text.Json;
using System.Text.Json.Serialization;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;
using Seiton.Update.Sources;
using Seiton.Update.Validators;

namespace Seiton.Update.Commands;

internal static class ContextTypesCommands
{
    private static readonly JsonSerializerOptions MergeWriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
        return 0; // warning only, not an error
    }

    public static int MergeSources(string repoRoot)
    {
        var validator = new ContextTypesValidator();
        var result = validator.Validate(repoRoot);

        if (!result.HasFindings)
        {
            UpdateLogger.Info("[merge:context-types:sources] context-types.json is already up to date with docs.");
            return 0;
        }

        // Warn about new contexts (must be added manually)
        foreach (var ctx in result.NewContexts)
        {
            UpdateLogger.Warn($"[merge:context-types:sources] new context '{ctx}' found in docs — add it manually to context-types.json.");
        }

        // Merge new top-level properties into existing contexts (depth-1 paths only)
        var topLevelNewProps = result.NewProperties
            .Where(static p => !p.DotPath.Contains('.', StringComparison.Ordinal))
            .ToList();

        var nestedNewProps = result.NewProperties
            .Where(static p => p.DotPath.Contains('.', StringComparison.Ordinal))
            .ToList();

        foreach (var prop in nestedNewProps)
        {
            UpdateLogger.Warn($"[merge:context-types:sources] nested property '{prop.ContextName}.{prop.DotPath}' (type: {prop.Type}) needs manual review in context-types.json.");
        }

        if (topLevelNewProps.Count == 0 && result.NewContexts.Count == 0)
        {
            UpdateLogger.Info("[merge:context-types:sources] no top-level property changes to merge automatically.");
            return 0;
        }

        if (topLevelNewProps.Count == 0)
        {
            UpdateLogger.Info("[merge:context-types:sources] no top-level property changes to merge automatically.");
            return 0;
        }

        // Load and update the existing model
        var sourcePath = ContextTypesSourcePathResolver.ResolvePrimary(repoRoot);
        var sourceParser = new ContextTypesSourceParser();
        var model = sourceParser.Parse(sourcePath);

        // Group new props by context name
        var newByContext = topLevelNewProps
            .GroupBy(static p => p.ContextName, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.ToList(), StringComparer.Ordinal);

        var updatedContexts = new List<ContextEntry>();
        foreach (var ctx in model.Contexts)
        {
            if (!newByContext.TryGetValue(ctx.Name, out var newProps))
            {
                updatedContexts.Add(ctx);
                continue;
            }

            // Add new properties to the context, sorted alphabetically
            var existingProps = ctx.Properties?.ToList() ?? [];
            foreach (var prop in newProps)
            {
                existingProps.Add(new ContextPropertyEntry(prop.DotPath, prop.Type));
                UpdateLogger.Info($"[merge:context-types:sources] adding {ctx.Name}.{prop.DotPath} (type: {prop.Type}) to context-types.json.");
            }

            existingProps.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            updatedContexts.Add(ctx with { Properties = existingProps });
        }

        var updatedModel = new ContextTypesModel(updatedContexts);
        var json = JsonSerializer.Serialize(updatedModel, MergeWriteOptions);
        File.WriteAllText(sourcePath, TextNormalization.NormalizeToLf(json) + "\n");

        UpdateLogger.Info($"[merge:context-types:sources] merged {topLevelNewProps.Count} new top-level property path(s) into context-types.json.");
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
