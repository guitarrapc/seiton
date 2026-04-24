using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;

namespace Seiton.Update.Sources;

internal sealed class IanaTimeZonesFetcher
{
    private const string TzdataZiUrl = "https://data.iana.org/time-zones/tzdb/tzdata.zi";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<SourceManifestEntry> FetchAsync(string repoRoot)
    {
        await FetchSourceFilesAsync(repoRoot);
        ParseLocalSourceFiles(repoRoot);
        MergeParsedSources(repoRoot);

        var paths = Paths(repoRoot);
        var rawHash = ComputeSha256(File.ReadAllText(paths.RawTzdataZiPath));

        return new SourceManifestEntry
        {
            Dataset = "iana-timezones",
            SourceUrls = [TzdataZiUrl],
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            RawFileHashes = new Dictionary<string, string>
            {
                [Path.GetFileName(paths.RawTzdataZiPath)] = rawHash,
            },
        };
    }

    public async Task FetchSourceFilesAsync(string repoRoot)
    {
        UpdateLogger.Info("[fetch:iana-timezones:sources] downloading IANA tzdata.zi...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var content = await client.GetStringAsync(TzdataZiUrl);
        var hash = ComputeSha256(content);
        UpdateLogger.Info($"[fetch:iana-timezones:sources] downloaded tzdata.zi={content.Length} bytes ({hash[..16]}...)");

        var paths = Paths(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RawTzdataZiPath)!);

        File.WriteAllText(paths.RawTzdataZiPath, TextNormalization.NormalizeToLf(content));

        UpdateLogger.Info($"[fetch:iana-timezones:sources] wrote {paths.RawTzdataZiPath}");
    }

    public void ParseLocalSourceFiles(string repoRoot)
    {
        var paths = Paths(repoRoot);
        if (!File.Exists(paths.RawTzdataZiPath))
        {
            throw new FileNotFoundException(
                "IANA timezones raw source files are missing. Run fetch-iana-timezones-sources first.",
                paths.RawTzdataZiPath);
        }

        UpdateLogger.Info("[parse:iana-timezones:sources] parsing local raw source files...");

        var ziContent = File.ReadAllText(paths.RawTzdataZiPath);
        var parser = new IanaTimeZonesZiParser();
        var result = parser.Parse(ziContent);

        var parsed = new
        {
            schemaVersion = 1,
            source = "iana-tzdb-tzdata-zi",
            version = result.Version,
            zones = result.Zones,
            links = result.Links,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(paths.ParsedPath)!);
        File.WriteAllText(paths.ParsedPath, TextNormalization.NormalizeToLf(JsonSerializer.Serialize(parsed, JsonOptions)));

        UpdateLogger.Info($"[parse:iana-timezones:sources] wrote {paths.ParsedPath} (version={result.Version}, zones={result.Zones.Count}, links={result.Links.Count})");
    }

    public void MergeParsedSources(string repoRoot)
    {
        var paths = Paths(repoRoot);
        if (!File.Exists(paths.ParsedPath))
        {
            throw new FileNotFoundException(
                "IANA timezones parsed source files are missing. Run parse-iana-timezones-sources first.",
                paths.ParsedPath);
        }

        UpdateLogger.Info("[merge:iana-timezones:sources] merging parsed sources...");

        var parsedText = File.ReadAllText(paths.ParsedPath);
        var parsed = JsonSerializer.Deserialize<ParsedSnapshot>(parsedText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Invalid parsed IANA timezones snapshot: {paths.ParsedPath}");

        var zones = (parsed.Zones ?? [])
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var links = (parsed.Links ?? [])
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var snapshot = new
        {
            schemaVersion = 1,
            source = "iana-official-merged-snapshot",
            version = parsed.Version ?? string.Empty,
            zoneIds = zones,
            linkIds = links,
        };

        var snapshotJson = TextNormalization.NormalizeToLf(JsonSerializer.Serialize(snapshot, JsonOptions));
        var existing = File.Exists(paths.MergedSnapshotPath)
            ? TextNormalization.NormalizeToLf(File.ReadAllText(paths.MergedSnapshotPath))
            : string.Empty;

        if (!string.Equals(existing, snapshotJson, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.MergedSnapshotPath)!);
            File.WriteAllText(paths.MergedSnapshotPath, snapshotJson);
            UpdateLogger.Info($"[merge:iana-timezones:sources] updated {paths.MergedSnapshotPath}");
        }
        else
        {
            UpdateLogger.Info("[merge:iana-timezones:sources] snapshot already up to date.");
        }
    }

    private static IanaTimeZonesPaths Paths(string repoRoot)
    {
        var baseDir = Path.Combine(repoRoot, "data", "sources", "iana-timezones", "iana");
        return new IanaTimeZonesPaths
        {
            RawTzdataZiPath = Path.Combine(baseDir, "raw", "tzdata.zi"),
            ParsedPath = Path.Combine(baseDir, "parsed", "iana-timezone-ids.json"),
            MergedSnapshotPath = Path.Combine(baseDir, "iana_timezones.json"),
        };
    }

    private static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    private sealed class IanaTimeZonesPaths
    {
        public string RawTzdataZiPath { get; set; } = string.Empty;
        public string ParsedPath { get; set; } = string.Empty;
        public string MergedSnapshotPath { get; set; } = string.Empty;
    }

    private sealed class ParsedSnapshot
    {
        public string? Version { get; set; }
        public List<string>? Zones { get; set; }
        public List<string>? Links { get; set; }
    }
}
