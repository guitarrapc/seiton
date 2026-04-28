using System.Text.Json;
using Seiton.Update.Generators;
using Seiton.Update.Model;

namespace Seiton.Update.Services;

internal sealed class EventPayloadTypesSyncService
{
    private readonly EventPayloadTypesCSharpGenerator generator = new();

    public bool Sync(string repoRoot)
    {
        var sourcePath = ResolveSourcePath(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "EventPayloadTypes.g.cs");

        var model = ParseSource(sourcePath);
        var generated = generator.Generate(model);

        var current = File.Exists(outputPath)
            ? TextNormalization.NormalizeToLf(File.ReadAllText(outputPath))
            : string.Empty;

        if (string.Equals(current, generated, StringComparison.Ordinal))
            return false;

        File.WriteAllText(outputPath, generated);
        return true;
    }

    public bool IsUpToDate(string repoRoot)
    {
        var sourcePath = ResolveSourcePath(repoRoot);
        var outputPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "EventPayloadTypes.g.cs");
        if (!File.Exists(outputPath))
            return false;

        var model = ParseSource(sourcePath);
        var generated = generator.Generate(model);
        var current = TextNormalization.NormalizeToLf(File.ReadAllText(outputPath));
        return string.Equals(current, generated, StringComparison.Ordinal);
    }

    private static string ResolveSourcePath(string repoRoot)
    {
        // Prefer new canonical path; fall back to legacy hand-written path
        var canonicalPath = Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "event_payload_types.json");
        if (File.Exists(canonicalPath))
        {
            return canonicalPath;
        }

        var legacyPath = EventPayloadTypesSourcePathResolver.ResolveLegacy(repoRoot);
        if (File.Exists(legacyPath))
        {
            return legacyPath;
        }

        throw new FileNotFoundException(
            "event_payload_types.json not found. Run fetch-event-payload-types first.",
            canonicalPath);
    }

    private static EventPayloadTypesModel ParseSource(string sourcePath)
    {
        var json = File.ReadAllText(sourcePath);
        return JsonSerializer.Deserialize<EventPayloadTypesModel>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }) ?? throw new InvalidOperationException($"Failed to parse {sourcePath}");
    }
}
