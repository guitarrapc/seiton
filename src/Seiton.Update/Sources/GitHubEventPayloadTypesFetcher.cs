using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Sources;

internal sealed class GitHubEventPayloadTypesFetcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<SourceManifestEntry> FetchAsync(string repoRoot)
    {
        await FetchSourceFilesAsync(repoRoot);
        ParseLocalSourceFiles(repoRoot);

        var rawPath = Path.Combine(EventPayloadTypesSourcePathResolver.ResolveRawDir(repoRoot), "webhook-events-and-payloads.html");
        var docsHash = SourceContentHasher.ComputeSha256(File.ReadAllText(rawPath));
        var sourceUrls = ManifestSourceUrls.Resolve(repoRoot, "event-payload-types", 1).ToList();

        return new SourceManifestEntry
        {
            Dataset = "event-payload-types",
            SourceUrls = sourceUrls,
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            RawFileHashes = new Dictionary<string, string>
            {
                ["webhook-events-and-payloads.html"] = docsHash,
            },
        };
    }

    public async Task FetchSourceFilesAsync(string repoRoot)
    {
        UpdateLogger.Info("[fetch:event-payload-types:sources] downloading GitHub Docs webhook-events-and-payloads page...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var docsUrl = ManifestSourceUrls.ResolveSingle(repoRoot, "event-payload-types");
        var htmlContent = await client.GetStringAsync(docsUrl);
        var hash = SourceContentHasher.ComputeSha256(htmlContent);
        UpdateLogger.Info($"[fetch:event-payload-types:sources] downloaded {htmlContent.Length} bytes ({hash[..16]}...)");

        var rawDir = EventPayloadTypesSourcePathResolver.ResolveRawDir(repoRoot);
        Directory.CreateDirectory(rawDir);
        var rawPath = Path.Combine(rawDir, "webhook-events-and-payloads.html");
        File.WriteAllText(rawPath, TextNormalization.NormalizeToLf(htmlContent));

        UpdateLogger.Info($"[fetch:event-payload-types:sources] wrote {rawPath}");
    }

    public void ParseLocalSourceFiles(string repoRoot)
    {
        var rawPath = EventPayloadTypesSourcePathResolver.ResolveRaw(repoRoot);

        UpdateLogger.Info("[parse:event-payload-types:sources] parsing local raw source file...");

        var htmlContent = File.ReadAllText(rawPath);
        var parser = new WebhookEventPayloadDocsParser();
        var model = parser.Parse(htmlContent);

        var rawSources = Stage2ArtifactRawSources.FromFiles((rawPath, Path.GetFileName(rawPath)));
        var modelWithMeta = new EventPayloadTypesModel(model.SchemaVersion, model.Source, model.Events, rawSources);

        UpdateLogger.Info($"[parse:event-payload-types:sources] parsed {model.Events.Count} events.");

        // Write parsed JSON
        var parsedDir = EventPayloadTypesSourcePathResolver.ResolveParsedDir(repoRoot);
        Directory.CreateDirectory(parsedDir);
        var parsedPath = Path.Combine(parsedDir, "parsed-event-payload-types.json");
        var parsedJson = TextNormalization.NormalizeToLf(JsonSerializer.Serialize(modelWithMeta, JsonOptions));
        File.WriteAllText(parsedPath, parsedJson);
        UpdateLogger.Info($"[parse:event-payload-types:sources] wrote {parsedPath}");

        // Write canonical snapshot
        var primaryDir = EventPayloadTypesSourcePathResolver.ResolvePrimaryDir(repoRoot);
        Directory.CreateDirectory(primaryDir);
        var primaryPath = Path.Combine(primaryDir, "event_payload_types.json");
        var primaryJson = TextNormalization.NormalizeToLf(JsonSerializer.Serialize(modelWithMeta, JsonOptions));

        var existing = File.Exists(primaryPath)
            ? TextNormalization.NormalizeToLf(File.ReadAllText(primaryPath))
            : string.Empty;

        if (!string.Equals(existing, primaryJson, StringComparison.Ordinal))
        {
            File.WriteAllText(primaryPath, primaryJson);
            UpdateLogger.Info($"[parse:event-payload-types:sources] updated {primaryPath}");
        }
        else
        {
            UpdateLogger.Info("[parse:event-payload-types:sources] snapshot already up to date.");
        }
    }
}
