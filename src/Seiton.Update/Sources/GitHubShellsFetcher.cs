using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Sources;

internal sealed class GitHubShellsFetcher
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

        var rawDir = ShellsSourcePathResolver.ResolveRawDir(repoRoot);
        var rawPath = Path.Combine(rawDir, "supported-shells.md");
        var docsHash = SourceContentHasher.ComputeSha256(File.ReadAllText(rawPath));
        var sourceUrls = ManifestSourceUrls.Resolve(repoRoot, "shells", 1).ToList();

        return new SourceManifestEntry
        {
            Dataset = "shells",
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
        UpdateLogger.Info("[fetch:shells:sources] downloading official GitHub source files...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var docsUrl = ManifestSourceUrls.ResolveSingle(repoRoot, "shells");
        var docsContent = await client.GetStringAsync(docsUrl);
        var docsHash = SourceContentHasher.ComputeSha256(docsContent);
        UpdateLogger.Info($"[fetch:shells:sources] downloaded docs={docsContent.Length} bytes ({docsHash[..16]}...)");

        var rawDir = ShellsSourcePathResolver.ResolveRawDir(repoRoot);
        Directory.CreateDirectory(rawDir);

        var rawPath = Path.Combine(rawDir, "supported-shells.md");
        File.WriteAllText(rawPath, TextNormalization.NormalizeToLf(docsContent));

        UpdateLogger.Info($"[fetch:shells:sources] wrote {rawPath}");
    }

    public void ParseLocalSourceFiles(string repoRoot)
    {
        var rawDir = ShellsSourcePathResolver.ResolveRawDir(repoRoot);
        var rawPath = Path.Combine(rawDir, "supported-shells.md");
        if (!File.Exists(rawPath))
        {
            throw new FileNotFoundException(
                "Shells raw source files are missing. Run fetch-shells-sources first.",
                rawPath);
        }

        UpdateLogger.Info("[parse:shells:sources] parsing local raw source files...");

        var docsText = File.ReadAllText(rawPath);
        var parser = new GitHubDocsSupportedShellsMarkdownParser();
        var rows = parser.Parse(docsText);

        var snapshot = new ShellsSnapshotRoot
        {
            SchemaVersion = 1,
            Source = "github-docs-supported-shells-reusable",
            RawSources = Stage2ArtifactRawSources.FromFiles((rawPath, Path.GetFileName(rawPath))),
            Shells = rows.Select(static r => new ShellsSnapshotShell
            {
                Name = r.Name,
                Platforms = r.Platforms.ToList(),
                Command = r.Command,
            }).ToList(),
        };

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var parsedDir = ShellsSourcePathResolver.ResolveParsedDir(repoRoot);
        Directory.CreateDirectory(parsedDir);

        var parsedPath = Path.Combine(parsedDir, "shells.json");
        File.WriteAllText(parsedPath, TextNormalization.NormalizeToLf(json + "\n"));

        UpdateLogger.Info($"[parse:shells:sources] wrote {parsedPath} ({rows.Count} shells)");
    }

    public void MergeParsedSources(string repoRoot)
    {
        var parsedDir = ShellsSourcePathResolver.ResolveParsedDir(repoRoot);
        var parsedPath = Path.Combine(parsedDir, "shells.json");
        if (!File.Exists(parsedPath))
        {
            throw new FileNotFoundException(
                "Shells parsed source files are missing. Run parse-shells-sources first.",
                parsedPath);
        }

        UpdateLogger.Info("[merge:shells:sources] merging parsed snapshot into canonical shells.json...");

        var normalized = TextNormalization.NormalizeToLf(File.ReadAllText(parsedPath));
        if (!normalized.EndsWith("\n", StringComparison.Ordinal))
        {
            normalized += "\n";
        }

        var primaryPath = Path.Combine(ShellsSourcePathResolver.ResolvePrimaryDir(repoRoot), "shells.json");
        var existing = File.Exists(primaryPath)
            ? TextNormalization.NormalizeToLf(File.ReadAllText(primaryPath))
            : string.Empty;

        if (!string.Equals(existing, normalized, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
            File.WriteAllText(primaryPath, normalized);
            UpdateLogger.Info($"[merge:shells:sources] wrote {primaryPath}");
        }
        else
        {
            UpdateLogger.Info("[merge:shells:sources] canonical snapshot already up to date.");
        }
    }

    private sealed class ShellsSnapshotRoot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<RawSourceRef>? RawSources { get; set; }
        public List<ShellsSnapshotShell>? Shells { get; set; }
    }

    private sealed class ShellsSnapshotShell
    {
        public string? Name { get; set; }
        public List<string>? Platforms { get; set; }
        public string? Command { get; set; }
    }
}
