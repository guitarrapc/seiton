using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Services;

internal sealed class WebhookManifestService
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SourceManifest Load(string repoRoot)
    {
        var path = ManifestPath(repoRoot);
        if (!File.Exists(path))
        {
            return SourceManifest.Empty;
        }

        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SourceManifest>(text, JsonOptions)
            ?? SourceManifest.Empty;
    }

    public SourceManifest Upsert(SourceManifest manifest, SourceManifestEntry entry)
    {
        manifest.Entries = manifest.Entries
            .Where(x => !string.Equals(x.Dataset, entry.Dataset, StringComparison.Ordinal))
            .Concat([entry])
            .OrderBy(static x => x.Dataset, StringComparer.Ordinal)
            .ToList();
        return manifest;
    }

    public void Save(string repoRoot, SourceManifest manifest)
    {
        var path = ManifestPath(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(path, json.Replace("\r\n", "\n"));
    }

    static string ManifestPath(string repoRoot) =>
        Path.Combine(repoRoot, "data", "sources", "manifest.json");
}
