using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Sources;

internal sealed class GitHubExpectedKeysFetcher
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

        var rawDir = Services.ExpectedKeysSourcePathResolver.ResolveRawDir(repoRoot);
        var rawPath = Path.Combine(rawDir, "workflow-syntax.md");
        var docsHash = SourceContentHasher.ComputeSha256(File.ReadAllText(rawPath));
        var sourceUrls = ManifestSourceUrls.Resolve(repoRoot, "expected-keys", 1).ToList();

        return new SourceManifestEntry
        {
            Dataset = "expected-keys",
            SourceUrls = sourceUrls,
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            RawFileHashes = new Dictionary<string, string>
            {
                [Path.GetFileName(rawPath)] = docsHash,
            },
        };
    }

    public async Task FetchSourceFilesAsync(string repoRoot)
    {
        UpdateLogger.Info("[fetch:expected-keys:sources] downloading official GitHub source files...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var docsUrl = ManifestSourceUrls.ResolveSingle(repoRoot, "expected-keys");
        var docsContent = await client.GetStringAsync(docsUrl);
        var docsHash = SourceContentHasher.ComputeSha256(docsContent);
        UpdateLogger.Info($"[fetch:expected-keys:sources] downloaded docs={docsContent.Length} bytes ({docsHash[..16]}...)");

        var rawDir = Services.ExpectedKeysSourcePathResolver.ResolveRawDir(repoRoot);
        Directory.CreateDirectory(rawDir);

        var rawPath = Path.Combine(rawDir, "workflow-syntax.md");
        File.WriteAllText(rawPath, TextNormalization.NormalizeToLf(docsContent));

        UpdateLogger.Info($"[fetch:expected-keys:sources] wrote {rawPath}");
    }

    public void ParseLocalSourceFiles(string repoRoot)
    {
        var rawDir = Services.ExpectedKeysSourcePathResolver.ResolveRawDir(repoRoot);
        var rawPath = Path.Combine(rawDir, "workflow-syntax.md");
        if (!File.Exists(rawPath))
        {
            throw new FileNotFoundException(
                "Expected keys raw source files are missing. Run fetch-expected-keys-sources first.",
                rawPath);
        }

        UpdateLogger.Info("[parse:expected-keys:sources] parsing local raw source files...");

        var docsText = File.ReadAllText(rawPath);
        var parser = new WorkflowSyntaxExpectedKeysParser();
        var model = parser.Parse(docsText);

        // Serialize to canonical snapshot JSON
        var snapshot = new ExpectedKeysSnapshot
        {
            SchemaVersion = 1,
            Source = "github-workflow-syntax-docs-raw",
            RawSources = Stage2ArtifactRawSources.FromFiles((rawPath, Path.GetFileName(rawPath))),
            Sections = model.Sections.Select(static s => new ExpectedKeysSnapshotSection
            {
                Name = s.Name,
                Description = s.Description,
                Keys = s.Keys.ToList(),
            }).ToList(),
        };

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var parsedDir = ExpectedKeysSourcePathResolver.ResolveParsedDir(repoRoot);
        Directory.CreateDirectory(parsedDir);

        var parsedPath = Path.Combine(parsedDir, "expected-keys.json");
        File.WriteAllText(parsedPath, TextNormalization.NormalizeToLf(json + "\n"));

        UpdateLogger.Info($"[parse:expected-keys:sources] wrote {parsedPath} ({model.Sections.Count} sections)");
    }

    public void MergeParsedSources(string repoRoot)
    {
        var parsedDir = ExpectedKeysSourcePathResolver.ResolveParsedDir(repoRoot);
        var parsedPath = Path.Combine(parsedDir, "expected-keys.json");
        if (!File.Exists(parsedPath))
        {
            throw new FileNotFoundException(
                "Expected keys parsed source files are missing. Run parse-expected-keys-sources first.",
                parsedPath);
        }

        UpdateLogger.Info("[merge:expected-keys:sources] merging parsed snapshot into canonical expected-keys.json...");

        var parsedText = File.ReadAllText(parsedPath);
        var parsed = JsonSerializer.Deserialize<ExpectedKeysSnapshot>(parsedText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Invalid parsed expected-keys snapshot: {parsedPath}");

        var sections = parsed.Sections?.ToList() ?? [];

        // Merge supplemental sections (hand-written, not from docs)
        var supplementalPath = ExpectedKeysSourcePathResolver.ResolveSupplementalKeys(repoRoot);
        if (File.Exists(supplementalPath))
        {
            var supText = File.ReadAllText(supplementalPath);
            var supDoc = JsonSerializer.Deserialize<SupplementalKeysSnapshot>(supText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            if (supDoc?.Sections is { Count: > 0 })
            {
                var existingNames = new HashSet<string>(sections.Select(s => s.Name ?? ""), StringComparer.Ordinal);
                var added = 0;
                foreach (var supSection in supDoc.Sections)
                {
                    if (!string.IsNullOrEmpty(supSection.Name) && existingNames.Add(supSection.Name!))
                    {
                        sections.Add(supSection);
                        added++;
                    }
                }

                sections.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
                UpdateLogger.Info($"[merge:expected-keys:sources] merged {added} supplemental section(s) from {Path.GetFileName(supplementalPath)}");
            }
        }

        parsed.Sections = sections;
        parsed.Source = "github-workflow-syntax-docs-merged";

        var mergedJson = TextNormalization.NormalizeToLf(JsonSerializer.Serialize(parsed, JsonOptions) + "\n");

        var primaryPath = Path.Combine(ExpectedKeysSourcePathResolver.ResolvePrimaryDir(repoRoot), "expected-keys.json");
        var existing = File.Exists(primaryPath)
            ? TextNormalization.NormalizeToLf(File.ReadAllText(primaryPath))
            : string.Empty;

        if (!string.Equals(existing, mergedJson, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
            File.WriteAllText(primaryPath, mergedJson);
            UpdateLogger.Info($"[merge:expected-keys:sources] wrote {primaryPath}");
        }
        else
        {
            UpdateLogger.Info("[merge:expected-keys:sources] canonical snapshot already up to date.");
        }
    }

    private sealed class SupplementalKeysSnapshot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<ExpectedKeysSnapshotSection>? Sections { get; set; }
    }

    private sealed class ExpectedKeysSnapshot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<RawSourceRef>? RawSources { get; set; }
        public List<ExpectedKeysSnapshotSection>? Sections { get; set; }
    }

    private sealed class ExpectedKeysSnapshotSection
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? Keys { get; set; }
    }
}
