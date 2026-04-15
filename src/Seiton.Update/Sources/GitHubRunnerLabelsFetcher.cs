using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;

namespace Seiton.Update.Sources;

internal sealed class GitHubRunnerLabelsFetcher
{
    const string DocsSourceUrl = "https://docs.github.com/en/actions/reference/runners/github-hosted-runners.md";

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
            Dataset = "runner-labels",
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
        UpdateLogger.Info("[fetch:runner-labels:sources] downloading official GitHub-hosted runners reference...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var docsContent = await client.GetStringAsync(DocsSourceUrl);
        var docsHash = ComputeSha256(docsContent);
        UpdateLogger.Info($"[fetch:runner-labels:sources] downloaded docs={docsContent.Length} bytes ({docsHash[..16]}...)");

        var paths = Paths(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RawDocsPath)!);

        File.WriteAllText(paths.RawDocsPath, TextNormalization.NormalizeToLf(docsContent));

        UpdateLogger.Info($"[fetch:runner-labels:sources] wrote {paths.RawDocsPath}");
    }

    public void ParseLocalSourceFiles(string repoRoot)
    {
        var paths = Paths(repoRoot);
        if (!File.Exists(paths.RawDocsPath))
        {
            throw new FileNotFoundException(
                "Runner-labels raw source files are missing. Run fetch-runner-labels-sources first.",
                paths.RawDocsPath);
        }

        UpdateLogger.Info("[parse:runner-labels:sources] parsing local raw source files...");

        var docsText = File.ReadAllText(paths.RawDocsPath);
        var parser = new GitHubDocsRunnerLabelsMarkdownParser();
        var labels = parser.ParseSupportedRunnerLabels(docsText);

        var parsed = new ParsedRunnerLabelsSnapshot
        {
            SchemaVersion = 1,
            Source = "github-hosted-runners-docs-rendered",
            Labels = labels
                .OrderBy(static x => x.Label, StringComparer.Ordinal)
                .Select(static x => new ParsedRunnerLabelEntry
                {
                    Label = x.Label,
                    IsPreview = x.IsPreview,
                })
                .ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(paths.ParsedDocsPath)!);
        File.WriteAllText(paths.ParsedDocsPath, TextNormalization.NormalizeToLf(JsonSerializer.Serialize(parsed, JsonOptions)));

        UpdateLogger.Info($"[parse:runner-labels:sources] wrote {paths.ParsedDocsPath}");
    }

    public void MergeParsedSources(string repoRoot)
    {
        var paths = Paths(repoRoot);
        if (!File.Exists(paths.ParsedDocsPath))
        {
            throw new FileNotFoundException(
                "Runner-labels parsed source files are missing. Run parse-runner-labels-sources first.",
                paths.ParsedDocsPath);
        }

        UpdateLogger.Info("[merge:runner-labels:sources] merging parsed sources...");

        var parsedText = File.ReadAllText(paths.ParsedDocsPath);
        var parsed = JsonSerializer.Deserialize<ParsedRunnerLabelsSnapshot>(parsedText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Invalid parsed runner-labels snapshot: {paths.ParsedDocsPath}");

        var labels = parsed.Labels
            .Where(static x => !string.IsNullOrWhiteSpace(x.Label))
            .GroupBy(static x => x.Label, StringComparer.Ordinal)
            .Select(static g => new
            {
                Label = g.Key,
                IsPreview = g.Any(static x => x.IsPreview),
            })
            .OrderBy(static x => x.Label, StringComparer.Ordinal)
            .ToArray();

        var snapshot = new
        {
            schemaVersion = 1,
            source = "github-official-merged-snapshot",
            stableLabels = labels
                .Where(static x => !x.IsPreview)
                .Select(static x => x.Label)
                .ToArray(),
            previewLabels = labels
                .Where(static x => x.IsPreview)
                .Select(static x => x.Label)
                .ToArray(),
        };

        var snapshotJson = TextNormalization.NormalizeToLf(JsonSerializer.Serialize(snapshot, JsonOptions));
        var existing = File.Exists(paths.MergedSnapshotPath)
            ? TextNormalization.NormalizeToLf(File.ReadAllText(paths.MergedSnapshotPath))
            : string.Empty;

        if (!string.Equals(existing, snapshotJson, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.MergedSnapshotPath)!);
            File.WriteAllText(paths.MergedSnapshotPath, snapshotJson);
            UpdateLogger.Info($"[merge:runner-labels:sources] updated {paths.MergedSnapshotPath}");
        }
        else
        {
            UpdateLogger.Info("[merge:runner-labels:sources] snapshot already up to date.");
        }
    }

    static RunnerLabelsPaths Paths(string repoRoot)
    {
        var baseDir = Path.Combine(repoRoot, "data", "sources", "runner-labels", "github");
        return new RunnerLabelsPaths
        {
            RawDocsPath = Path.Combine(baseDir, "raw", "github-hosted-runners.docs.md"),
            ParsedDocsPath = Path.Combine(baseDir, "parsed", "docs-runner-labels.json"),
            MergedSnapshotPath = Path.Combine(baseDir, "runner_labels.json"),
        };
    }

    static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    sealed class RunnerLabelsPaths
    {
        public string RawDocsPath { get; set; } = string.Empty;
        public string ParsedDocsPath { get; set; } = string.Empty;
        public string MergedSnapshotPath { get; set; } = string.Empty;
    }

    sealed class ParsedRunnerLabelsSnapshot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<ParsedRunnerLabelEntry> Labels { get; set; } = [];
    }

    sealed class ParsedRunnerLabelEntry
    {
        public string Label { get; set; } = string.Empty;
        public bool IsPreview { get; set; }
    }
}
