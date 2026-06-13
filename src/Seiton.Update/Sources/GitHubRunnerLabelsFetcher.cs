using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Sources;

internal sealed class GitHubRunnerLabelsFetcher
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
        MergeParsedSources(repoRoot);

        var paths = Paths(repoRoot);
        var docsHash = SourceContentHasher.ComputeSha256(File.ReadAllText(paths.RawDocsPath));
        var largerHash = SourceContentHasher.ComputeSha256(File.ReadAllText(paths.RawLargerRunnersPath));
        var sourceUrls = ManifestSourceUrls.Resolve(repoRoot, "runner-labels", 2).ToList();

        return new SourceManifestEntry
        {
            Dataset = "runner-labels",
            SourceUrls = sourceUrls,
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            RawFileHashes = new Dictionary<string, string>
            {
                [Path.GetFileName(paths.RawDocsPath)] = docsHash,
                [Path.GetFileName(paths.RawLargerRunnersPath)] = largerHash,
            },
        };
    }

    public async Task FetchSourceFilesAsync(string repoRoot)
    {
        UpdateLogger.Info("[fetch:runner-labels:sources] downloading official GitHub-hosted runners reference...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var urls = ManifestSourceUrls.Resolve(repoRoot, "runner-labels", 2);
        var docsContent = await client.GetStringAsync(urls[0]);
        var docsHash = SourceContentHasher.ComputeSha256(docsContent);
        UpdateLogger.Info($"[fetch:runner-labels:sources] downloaded docs={docsContent.Length} bytes ({docsHash[..16]}...)");

        var largerContent = await client.GetStringAsync(urls[1]);
        var largerHash = SourceContentHasher.ComputeSha256(largerContent);
        UpdateLogger.Info($"[fetch:runner-labels:sources] downloaded larger-runners={largerContent.Length} bytes ({largerHash[..16]}...)");

        var paths = Paths(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RawDocsPath)!);

        File.WriteAllText(paths.RawDocsPath, TextNormalization.NormalizeToLf(docsContent));
        File.WriteAllText(paths.RawLargerRunnersPath, TextNormalization.NormalizeToLf(largerContent));

        UpdateLogger.Info($"[fetch:runner-labels:sources] wrote {paths.RawDocsPath}");
        UpdateLogger.Info($"[fetch:runner-labels:sources] wrote {paths.RawLargerRunnersPath}");
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

        var parser = new GitHubDocsRunnerLabelsMarkdownParser();

        // Parse standard hosted runners
        var docsText = File.ReadAllText(paths.RawDocsPath);
        var labels = parser.ParseSupportedRunnerLabels(docsText);

        // Parse larger runners (if raw file exists)
        if (File.Exists(paths.RawLargerRunnersPath))
        {
            var largerText = File.ReadAllText(paths.RawLargerRunnersPath);
            var largerLabels = parser.ParseLargerRunnerLabels(largerText);
            labels = labels.Concat(largerLabels).ToArray();
        }

        var rawFileTuples = new List<(string fullPath, string fileName)>
        {
            (paths.RawDocsPath, Path.GetFileName(paths.RawDocsPath)),
        };
        if (File.Exists(paths.RawLargerRunnersPath))
        {
            rawFileTuples.Add((paths.RawLargerRunnersPath, Path.GetFileName(paths.RawLargerRunnersPath)));
        }

        var parsed = new ParsedRunnerLabelsSnapshot
        {
            SchemaVersion = 1,
            Source = "github-docs-hosted-and-larger-runners",
            RawSources = Stage2ArtifactRawSources.FromFiles(rawFileTuples.ToArray()),
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

        // Merge supplemental and curated deprecation labels (hand-written, not from docs)
        var supplementalStable = Array.Empty<string>();
        var supplementalPreview = Array.Empty<string>();
        if (File.Exists(paths.SupplementalLabelsPath))
        {
            var supText = File.ReadAllText(paths.SupplementalLabelsPath);
            var supDoc = JsonSerializer.Deserialize<JsonElement>(supText);
            if (supDoc.TryGetProperty("stableLabels", out var stableArr))
                supplementalStable = stableArr.EnumerateArray().Select(e => e.GetString()!).ToArray();
            if (supDoc.TryGetProperty("previewLabels", out var previewArr))
                supplementalPreview = previewArr.EnumerateArray().Select(e => e.GetString()!).ToArray();
            UpdateLogger.Info($"[merge:runner-labels:sources] merged {supplementalStable.Length + supplementalPreview.Length} supplemental labels from {Path.GetFileName(paths.SupplementalLabelsPath)}");
        }

        var deprecatedLabels = Array.Empty<string>();
        if (File.Exists(paths.DeprecatedLabelsPath))
        {
            var depText = File.ReadAllText(paths.DeprecatedLabelsPath);
            var depDoc = JsonSerializer.Deserialize<JsonElement>(depText);
            if (depDoc.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Invalid deprecated-labels snapshot (expected JSON object): {paths.DeprecatedLabelsPath}");
            }

            if (depDoc.TryGetProperty("deprecatedLabels", out var deprecatedArr))
            {
                if (deprecatedArr.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException(
                        $"Invalid deprecated-labels snapshot ('deprecatedLabels' must be an array): {paths.DeprecatedLabelsPath}");
                }

                deprecatedLabels = deprecatedArr.EnumerateArray()
                    .Select(static e => e.GetString()!)
                    .Where(static x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static x => x, StringComparer.Ordinal)
                    .ToArray();
            }

            UpdateLogger.Info($"[merge:runner-labels:sources] merged {deprecatedLabels.Length} deprecated labels from {Path.GetFileName(paths.DeprecatedLabelsPath)}");
        }

        var snapshot = new
        {
            schemaVersion = 1,
            source = "github-official-merged-snapshot",
            stableLabels = labels
                .Where(static x => !x.IsPreview)
                .Select(static x => x.Label)
                .Concat(supplementalStable)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static x => x, StringComparer.Ordinal)
                .ToArray(),
            previewLabels = labels
                .Where(static x => x.IsPreview)
                .Select(static x => x.Label)
                .Concat(supplementalPreview)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static x => x, StringComparer.Ordinal)
                .ToArray(),
            deprecatedLabels,
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

    private static RunnerLabelsPaths Paths(string repoRoot)
    {
        var baseDir = Path.Combine(repoRoot, "data", "sources", "runner-labels", "github");
        return new RunnerLabelsPaths
        {
            RawDocsPath = Path.Combine(baseDir, "raw", "github-hosted-runners.docs.md"),
            RawLargerRunnersPath = Path.Combine(baseDir, "raw", "larger-runners.docs.md"),
            ParsedDocsPath = Path.Combine(baseDir, "parsed", "docs-runner-labels.json"),
            SupplementalLabelsPath = Path.Combine(baseDir, "supplemental-labels.json"),
            DeprecatedLabelsPath = Path.Combine(baseDir, "deprecated-labels.json"),
            MergedSnapshotPath = Path.Combine(baseDir, "runner_labels.json"),
        };
    }

    private sealed class RunnerLabelsPaths
    {
        public string RawDocsPath { get; set; } = string.Empty;
        public string RawLargerRunnersPath { get; set; } = string.Empty;
        public string ParsedDocsPath { get; set; } = string.Empty;
        public string SupplementalLabelsPath { get; set; } = string.Empty;
        public string DeprecatedLabelsPath { get; set; } = string.Empty;
        public string MergedSnapshotPath { get; set; } = string.Empty;
    }

    private sealed class ParsedRunnerLabelsSnapshot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<RawSourceRef>? RawSources { get; set; }
        public List<ParsedRunnerLabelEntry> Labels { get; set; } = [];
    }

    private sealed class ParsedRunnerLabelEntry
    {
        public string Label { get; set; } = string.Empty;
        public bool IsPreview { get; set; }
    }
}
