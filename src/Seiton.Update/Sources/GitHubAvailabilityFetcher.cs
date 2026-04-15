using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;

namespace Seiton.Update.Sources;

internal sealed class GitHubAvailabilityFetcher
{
    const string DocsSourceUrl = "https://raw.githubusercontent.com/github/docs/main/content/actions/reference/workflows-and-actions/contexts.md";

    static readonly JsonSerializerOptions JsonOptions = new()
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
        var docsHash = ComputeSha256(File.ReadAllText(paths.RawDocsPath));

        return new SourceManifestEntry
        {
            Dataset = "availability",
            SourceUrls = [DocsSourceUrl],
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            RawFileHashes = new Dictionary<string, string>
            {
                [Path.GetFileName(paths.RawDocsPath)] = docsHash,
            },
        };
    }

    public async Task FetchSourceFilesAsync(string repoRoot)
    {
        UpdateLogger.Info("[fetch:availability:sources] downloading official GitHub source files...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var docsContent = await client.GetStringAsync(DocsSourceUrl);
        var docsHash = ComputeSha256(docsContent);
        UpdateLogger.Info($"[fetch:availability:sources] downloaded docs={docsContent.Length} bytes ({docsHash[..16]}...)");

        var paths = Paths(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RawDocsPath)!);

        File.WriteAllText(paths.RawDocsPath, docsContent.Replace("\r\n", "\n"));

        UpdateLogger.Info($"[fetch:availability:sources] wrote {paths.RawDocsPath}");
    }

    public void ParseLocalSourceFiles(string repoRoot)
    {
        var paths = Paths(repoRoot);
        if (!File.Exists(paths.RawDocsPath))
        {
            throw new FileNotFoundException(
                "Availability raw source files are missing. Run fetch-availability-sources first.",
                paths.RawDocsPath);
        }

        UpdateLogger.Info("[parse:availability:sources] parsing local raw source files...");

        var docsText = File.ReadAllText(paths.RawDocsPath);
        var parser = new GitHubDocsAvailabilityMarkdownParser();
        var map = parser.ParseWorkflowKeyContexts(docsText);

        var parsed = new ParsedAvailabilitySnapshot
        {
            SchemaVersion = 1,
            Source = "github-contexts-docs-raw",
            Entries = map
                .OrderBy(static x => x.Key, StringComparer.Ordinal)
                .Select(static x => new ParsedAvailabilityEntry
                {
                    WorkflowKey = x.Key,
                    Contexts = x.Value.ToList(),
                })
                .ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(paths.ParsedDocsPath)!);
        File.WriteAllText(paths.ParsedDocsPath, JsonSerializer.Serialize(parsed, JsonOptions).Replace("\r\n", "\n"));

        UpdateLogger.Info($"[parse:availability:sources] wrote {paths.ParsedDocsPath}");
    }

    public void MergeParsedSources(string repoRoot)
    {
        var paths = Paths(repoRoot);
        if (!File.Exists(paths.ParsedDocsPath))
        {
            throw new FileNotFoundException(
                "Availability parsed source files are missing. Run parse-availability-sources first.",
                paths.ParsedDocsPath);
        }

        UpdateLogger.Info("[merge:availability:sources] merging parsed sources...");

        var parsedText = File.ReadAllText(paths.ParsedDocsPath);
        var parsed = JsonSerializer.Deserialize<ParsedAvailabilitySnapshot>(parsedText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Invalid parsed availability snapshot: {paths.ParsedDocsPath}");

        var map = parsed.Entries.ToDictionary(
            static x => x.WorkflowKey,
            static x => (IReadOnlyList<string>)x.Contexts,
            StringComparer.Ordinal);

        var workflowRoots = ResolveContextSet(map, "run-name", "concurrency");
        var jobRoots = ResolveContextSet(map, "jobs.<job_id>.concurrency");
        var stepRoots = ResolveContextSet(map, "jobs.<job_id>.steps.run", "jobs.<job_id>.steps.if");

        var snapshot = new
        {
            schemaVersion = 1,
            source = "github-official-merged-snapshot",
            workflowRoots,
            jobRoots,
            stepRoots,
        };

        var snapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions).Replace("\r\n", "\n");
        var existing = File.Exists(paths.MergedSnapshotPath)
            ? File.ReadAllText(paths.MergedSnapshotPath).Replace("\r\n", "\n")
            : string.Empty;

        if (!string.Equals(existing, snapshotJson, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.MergedSnapshotPath)!);
            File.WriteAllText(paths.MergedSnapshotPath, snapshotJson);
            UpdateLogger.Info($"[merge:availability:sources] updated {paths.MergedSnapshotPath}");
        }
        else
        {
            UpdateLogger.Info("[merge:availability:sources] snapshot already up to date.");
        }
    }

    static string[] ResolveContextSet(IReadOnlyDictionary<string, IReadOnlyList<string>> map, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var values) && values.Count > 0)
            {
                return values
                    .Where(static x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
        }

        throw new InvalidDataException($"Required availability key not found in parsed snapshot. keys=[{string.Join(", ", keys)}]");
    }

    static AvailabilityPaths Paths(string repoRoot)
    {
        var baseDir = Path.Combine(repoRoot, "data", "sources", "availability", "github");
        return new AvailabilityPaths
        {
            RawDocsPath = Path.Combine(baseDir, "raw", "contexts.docs.md"),
            ParsedDocsPath = Path.Combine(baseDir, "parsed", "docs-context-availability.json"),
            MergedSnapshotPath = Path.Combine(baseDir, "availability.json"),
        };
    }

    static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    sealed class AvailabilityPaths
    {
        public string RawDocsPath { get; set; } = string.Empty;
        public string ParsedDocsPath { get; set; } = string.Empty;
        public string MergedSnapshotPath { get; set; } = string.Empty;
    }

    sealed class ParsedAvailabilitySnapshot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<ParsedAvailabilityEntry> Entries { get; set; } = [];
    }

    sealed class ParsedAvailabilityEntry
    {
        public string WorkflowKey { get; set; } = string.Empty;
        public List<string> Contexts { get; set; } = [];
    }
}
