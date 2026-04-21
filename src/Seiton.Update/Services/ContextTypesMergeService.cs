using System.Text.Json;
using System.Text.Json.Serialization;
using Seiton.Update.Model;
using Seiton.Update.Sources;

namespace Seiton.Update.Services;

/// <summary>
/// Merges the parsed docs snapshot (docs-contexts.json) with the hand-written override
/// (context-types-override.json) to produce the canonical context-types.json.
///
/// Merge rules per context:
///   1. Start with depth-1, non-placeholder documented properties from docs.
///   2. If a property has a matching entry in PropertyOverrides, use the override entry instead
///      (supports nested object schemas, type corrections, extra metadata).
///   3. Append UndocumentedProperties from the override (tagged with undocumented: true).
///   4. Sort all properties alphabetically by name.
///   5. Apply context-level Strict and DynamicPropertyType from the override.
///
/// Context ordering follows the order defined in context-types-override.json.
/// Contexts present in docs but absent from the override are added at the end with a warning.
/// </summary>
internal sealed class ContextTypesMergeService
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Merge docs snapshot + override → context-types.json.
    /// Returns true if the output file changed.
    /// </summary>
    public bool Merge(string repoRoot)
    {
        var parsedPath = GitHubContextTypesFetcher.Paths(repoRoot).ParsedDocsPath;
        if (!File.Exists(parsedPath))
        {
            throw new FileNotFoundException(
                "Parsed docs-contexts.json not found. Run fetch-context-types-sources and parse-context-types-sources first.",
                parsedPath);
        }

        var overridePath = ContextTypesOverridePathResolver.Resolve(repoRoot);
        if (!File.Exists(overridePath))
        {
            throw new FileNotFoundException(
                "context-types-override.json not found.",
                overridePath);
        }

        var parsedSnapshot = JsonSerializer.Deserialize<GitHubContextTypesFetcher.ParsedContextTypesSnapshot>(
            File.ReadAllText(parsedPath), ReadOptions)
            ?? throw new InvalidDataException($"Invalid docs-contexts.json: {parsedPath}");

        var overrideModel = JsonSerializer.Deserialize<ContextTypesOverrideModel>(
            File.ReadAllText(overridePath), ReadOptions)
            ?? throw new InvalidDataException($"Invalid context-types-override.json: {overridePath}");

        var docIndex = parsedSnapshot.Contexts.ToDictionary(c => c.Name, StringComparer.Ordinal);

        var mergedContexts = new List<ContextEntry>();
        var overrideNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var overrideCtx in overrideModel.ContextOverrides)
        {
            overrideNames.Add(overrideCtx.Name);
            docIndex.TryGetValue(overrideCtx.Name, out var docCtx);
            mergedContexts.Add(MergeContext(overrideCtx, docCtx));
        }

        // Contexts in docs but not in override → add with a warning
        foreach (var docCtx in parsedSnapshot.Contexts)
        {
            if (overrideNames.Contains(docCtx.Name))
                continue;

            UpdateLogger.Warn($"[merge:context-types] context '{docCtx.Name}' found in docs but has no override entry — add it to context-types-override.json.");
            mergedContexts.Add(BuildFromDocsOnly(docCtx));
        }

        var merged = new ContextTypesModel(mergedContexts);
        var outputPath = ContextTypesSourcePathResolver.ResolvePrimary(repoRoot);
        var json = TextNormalization.NormalizeToLf(JsonSerializer.Serialize(merged, WriteOptions)) + "\n";

        var existing = File.Exists(outputPath) ? File.ReadAllText(outputPath) : string.Empty;
        if (string.Equals(existing, json, StringComparison.Ordinal))
            return false;

        File.WriteAllText(outputPath, json);
        return true;
    }

    private static ContextEntry MergeContext(
        ContextOverrideEntry overrideCtx,
        GitHubContextTypesFetcher.ParsedContextEntry? docCtx)
    {
        // Build index of property overrides keyed by name
        var propOverrideMap = (overrideCtx.PropertyOverrides ?? [])
            .ToDictionary(static p => p.Name, StringComparer.Ordinal);

        // Extract depth-1 non-placeholder documented properties, one entry per top-level token
        var docDepth1 = GetDocDepth1Properties(docCtx);

        var properties = new List<ContextPropertyEntry>();

        // Documented properties — use override if present, else normalize from docs
        foreach (var (topToken, docType) in docDepth1)
        {
            if (propOverrideMap.TryGetValue(topToken, out var overrideProp))
            {
                properties.Add(overrideProp);
            }
            else
            {
                properties.Add(new ContextPropertyEntry(topToken, NormalizeType(docType)));
            }
        }

        // Undocumented properties — marked with undocumented: true
        foreach (var undoc in overrideCtx.UndocumentedProperties ?? [])
        {
            properties.Add(undoc with { Undocumented = true });
        }

        // Stable alphabetical sort by property name
        properties.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));

        return new ContextEntry(
            overrideCtx.Name,
            Strict: overrideCtx.Strict,
            DynamicPropertyType: overrideCtx.DynamicPropertyType,
            Properties: properties.Count > 0 ? properties : null);
    }

    /// <summary>
    /// Extract depth-1 non-placeholder properties from the docs entry.
    /// Returns (topToken, type) pairs, deduplicated by top token (first occurrence wins, which is the depth-1 path).
    /// Placeholder paths like &lt;env_name&gt; are skipped — they inform dynamicPropertyType, not properties.
    /// </summary>
    private static List<(string TopToken, string Type)> GetDocDepth1Properties(
        GitHubContextTypesFetcher.ParsedContextEntry? docCtx)
    {
        if (docCtx is null)
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string, string)>();

        foreach (var p in docCtx.Properties)
        {
            var dotIdx = p.Path.IndexOf('.', StringComparison.Ordinal);
            var topToken = dotIdx >= 0 ? p.Path[..dotIdx] : p.Path;

            // Skip placeholder tokens like <env_name>, <service_id>, etc.
            if (topToken.StartsWith("<", StringComparison.Ordinal))
                continue;

            // Deduplicate: first occurrence of each top token = the depth-1 entry
            if (!seen.Add(topToken))
                continue;

            result.Add((topToken, p.Type));
        }

        return result;
    }

    private static ContextEntry BuildFromDocsOnly(GitHubContextTypesFetcher.ParsedContextEntry docCtx)
    {
        var depth1 = GetDocDepth1Properties(docCtx)
            .Select(static p => new ContextPropertyEntry(p.TopToken, NormalizeType(p.Type)))
            .ToList();

        return new ContextEntry(
            docCtx.Name,
            Properties: depth1.Count > 0 ? depth1 : null);
    }

    /// <summary>
    /// Normalize GitHub Docs type names to the internal type strings used by ExprType.
    /// Docs uses "boolean"; our system uses "bool".
    /// </summary>
    private static string NormalizeType(string docType) =>
        docType switch
        {
            "boolean" => "bool",
            _ => docType,
        };
}
