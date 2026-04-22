using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Validators;

internal sealed class FunctionSpecsValidator
{
    /// <summary>
    /// Compare parsed function names from docs against function-specs.json.
    /// Returns unregistered function names (present in docs but missing from function-specs.json).
    /// </summary>
    public IReadOnlyList<string> Validate(string repoRoot)
    {
        var parsedPath = Path.Combine(repoRoot, "data", "sources", "function-specs", "github", "parsed", "docs-function-names.json");
        if (!File.Exists(parsedPath))
        {
            UpdateLogger.Warn("[validate:function-specs] parsed function names not found. Run fetch-function-specs-sources and parse-function-specs-sources first.");
            return [];
        }

        var parsedJson = File.ReadAllText(parsedPath);
        var parsed = JsonSerializer.Deserialize<GitHubFunctionNamesFetcher.ParsedFunctionNamesSnapshot>(parsedJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Invalid parsed function names snapshot: {parsedPath}");

        var specPath = FunctionSpecsSourcePathResolver.ResolvePrimary(repoRoot);
        var specJson = File.ReadAllText(specPath);
        var specModel = JsonSerializer.Deserialize<FunctionSpecsModel>(specJson, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
        }) ?? throw new InvalidDataException($"Invalid function-specs.json: {specPath}");

        var registeredNames = new HashSet<string>(
            specModel.Functions.Select(static f => f.Name),
            StringComparer.OrdinalIgnoreCase);

        var unregistered = parsed.FunctionNames
            .Where(name => !registeredNames.Contains(name))
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToList();

        return unregistered;
    }
}
