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

        var normalized = TextNormalization.NormalizeToLf(File.ReadAllText(parsedPath));
        if (!normalized.EndsWith("\n", StringComparison.Ordinal))
        {
            normalized += "\n";
        }

        var primaryPath = Path.Combine(ExpectedKeysSourcePathResolver.ResolvePrimaryDir(repoRoot), "expected-keys.json");
        var existing = File.Exists(primaryPath)
            ? TextNormalization.NormalizeToLf(File.ReadAllText(primaryPath))
            : string.Empty;

        if (!string.Equals(existing, normalized, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
            File.WriteAllText(primaryPath, normalized);
            UpdateLogger.Info($"[merge:expected-keys:sources] wrote {primaryPath}");
        }
        else
        {
            UpdateLogger.Info("[merge:expected-keys:sources] canonical snapshot already up to date.");
        }
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
