using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Validators;

/// <summary>
/// Compares context property paths parsed from GitHub Docs against the hand-written context-types.json.
/// Reports contexts or properties present in docs but absent from context-types.json.
/// </summary>
internal sealed class ContextTypesValidator
{
    private const string DynamicMarker = "__dynamic__";

    /// <summary>
    /// Validate context-types.json against the parsed docs snapshot.
    /// Returns a validation result describing new contexts and new properties found in docs.
    /// </summary>
    public ContextTypesValidationResult Validate(string repoRoot)
    {
        var parsedPath = GitHubContextTypesFetcher.Paths(repoRoot).ParsedDocsPath;
        if (!File.Exists(parsedPath))
        {
            UpdateLogger.Warn("[validate:context-types] parsed docs-contexts.json not found. Run fetch-context-types-sources and parse-context-types-sources first.");
            return new ContextTypesValidationResult();
        }

        var parsedJson = File.ReadAllText(parsedPath);
        var parsedSnapshot = JsonSerializer.Deserialize<GitHubContextTypesFetcher.ParsedContextTypesSnapshot>(parsedJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Invalid docs-contexts.json: {parsedPath}");

        var sourcePath = ContextTypesSourcePathResolver.ResolvePrimary(repoRoot);
        var sourceParser = new Parsers.ContextTypesSourceParser();
        var model = sourceParser.Parse(sourcePath);

        // Index existing contexts by name
        var existingContexts = model.Contexts.ToDictionary(
            static c => c.Name,
            static c => c,
            StringComparer.Ordinal);

        var newContexts = new List<string>();
        var newProperties = new List<NewContextTypeProperty>();

        foreach (var docCtx in parsedSnapshot.Contexts)
        {
            if (!existingContexts.TryGetValue(docCtx.Name, out var existingCtx))
            {
                newContexts.Add(docCtx.Name);
                continue;
            }

            // Build the set of normalized top-level path tokens for the existing context
            var existingTopLevel = BuildExistingTopLevelPaths(existingCtx);

            // Build the set of normalized top-level path tokens from docs, and identify new ones
            var docTopLevel = BuildDocTopLevelPaths(docCtx);

            foreach (var (topToken, fullPath, propType) in docTopLevel)
            {
                if (!existingTopLevel.Contains(topToken))
                {
                    newProperties.Add(new NewContextTypeProperty(docCtx.Name, fullPath, propType));
                }
            }
        }

        return new ContextTypesValidationResult
        {
            NewContexts = newContexts,
            NewProperties = newProperties,
        };
    }

    private static HashSet<string> BuildExistingTopLevelPaths(ContextEntry ctx)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        if (ctx.DynamicPropertyType is not null)
            set.Add(DynamicMarker);

        if (ctx.Properties is not null)
        {
            foreach (var prop in ctx.Properties)
                set.Add(prop.Name);
        }

        return set;
    }

    private static List<(string TopToken, string FullPath, string Type)> BuildDocTopLevelPaths(
        GitHubContextTypesFetcher.ParsedContextEntry docCtx)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string, string, string)>();

        foreach (var prop in docCtx.Properties)
        {
            // Top-level token = portion before first dot
            var dotIdx = prop.Path.IndexOf('.', StringComparison.Ordinal);
            var topToken = dotIdx >= 0 ? prop.Path[..dotIdx] : prop.Path;

            // Normalize placeholder keys like <env_name>, <job_id>, etc. to a single marker
            var normalized = (topToken.StartsWith("<", StringComparison.Ordinal) && topToken.EndsWith(">", StringComparison.Ordinal))
                ? DynamicMarker
                : topToken;

            // Only report each top-level token once, and skip dynamic markers (they match dynamicPropertyType)
            if (normalized == DynamicMarker || !seen.Add(normalized))
                continue;

            result.Add((normalized, prop.Path, prop.Type));
        }

        return result;
    }
}

internal sealed class ContextTypesValidationResult
{
    public IReadOnlyList<string> NewContexts { get; init; } = [];
    public IReadOnlyList<NewContextTypeProperty> NewProperties { get; init; } = [];

    public bool HasFindings => NewContexts.Count > 0 || NewProperties.Count > 0;
}

/// <summary>
/// A property (or context) found in GitHub Docs but absent from context-types.json.
/// DotPath is the path relative to the context root (without the context name prefix).
/// </summary>
internal sealed record NewContextTypeProperty(string ContextName, string DotPath, string Type);
