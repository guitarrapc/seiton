using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;

namespace Seiton.Update.Sources;

internal sealed class GitHubAvailabilityFetcher
{
    private const string DocsSourceUrl = "https://raw.githubusercontent.com/github/docs/main/content/actions/reference/workflows-and-actions/contexts.md";

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

        File.WriteAllText(paths.RawDocsPath, TextNormalization.NormalizeToLf(docsContent));

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
        File.WriteAllText(paths.ParsedDocsPath, TextNormalization.NormalizeToLf(JsonSerializer.Serialize(parsed, JsonOptions)));

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

        var workflowRoots = ResolveContextSet(map, "run-name", "concurrency", "env");
        var workflowCallOutputRoots = ResolveContextSet(map, "on.workflow_call.outputs.<output_id>.value");
        var jobRoots = ResolveContextSet(map, "jobs.<job_id>.concurrency", "jobs.<job_id>.env");
        var jobOutputRoots = ResolveContextSet(map, "jobs.<job_id>.outputs.<output_id>");
        var reusableWorkflowCallSecretsRoots = ResolveContextSet(map, "jobs.<job_id>.secrets.<secrets_id>");
        var strategyRoots = ResolveContextSet(map, "jobs.<job_id>.strategy");
        var stepRoots = ResolveContextSet(map, "jobs.<job_id>.steps.run", "jobs.<job_id>.steps.if");
        var stepIfRoots = ResolveContextSet(map, "jobs.<job_id>.steps.if");

        var snapshot = new
        {
            schemaVersion = 1,
            source = "github-official-merged-snapshot",
            workflowRoots,
            workflowCallOutputRoots,
            jobRoots,
            jobOutputRoots,
            reusableWorkflowCallSecretsRoots,
            strategyRoots,
            stepRoots,
            stepIfRoots,
        };

        var snapshotJson = TextNormalization.NormalizeToLf(JsonSerializer.Serialize(snapshot, JsonOptions)) + "\n";
        var existing = File.Exists(paths.MergedSnapshotPath)
            ? TextNormalization.NormalizeToLf(File.ReadAllText(paths.MergedSnapshotPath))
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

    private static string[] ResolveContextSet(IReadOnlyDictionary<string, IReadOnlyList<string>> map, params string[] keys)
    {
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var values))
            {
                foreach (var v in values)
                {
                    if (!string.IsNullOrWhiteSpace(v))
                        union.Add(v);
                }
            }
        }

        if (union.Count == 0)
            throw new InvalidDataException($"Required availability key not found in parsed snapshot. keys=[{string.Join(", ", keys)}]");

        return union.ToArray();
    }

    private static AvailabilityPaths Paths(string repoRoot)
    {
        var baseDir = Path.Combine(repoRoot, "data", "sources", "availability", "github");
        return new AvailabilityPaths
        {
            RawDocsPath = Path.Combine(baseDir, "raw", "contexts.docs.md"),
            ParsedDocsPath = Path.Combine(baseDir, "parsed", "docs-context-availability.json"),
            MergedSnapshotPath = Path.Combine(baseDir, "availability.json"),
        };
    }

    private static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    private sealed class AvailabilityPaths
    {
        public string RawDocsPath { get; set; } = string.Empty;
        public string ParsedDocsPath { get; set; } = string.Empty;
        public string MergedSnapshotPath { get; set; } = string.Empty;
    }

    private sealed class ParsedAvailabilitySnapshot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<ParsedAvailabilityEntry> Entries { get; set; } = [];
    }

    private sealed class ParsedAvailabilityEntry
    {
        public string WorkflowKey { get; set; } = string.Empty;
        public List<string> Contexts { get; set; } = [];
    }
}
