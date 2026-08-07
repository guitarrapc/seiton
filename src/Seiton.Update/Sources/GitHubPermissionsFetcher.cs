using System.Text.Json;
using System.Text.Json.Serialization;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Sources;

internal sealed class GitHubPermissionsFetcher
{
    /// <summary>
    /// Fetches and parses the reusable YAML permissions block (scopes and allowed values) from GitHub Docs.
    /// Provenance for fetch URL and raw bytes is recorded in <c>data/sources/manifest.json</c>;
    /// <see cref="ParsedPermissionsSnapshot.SourceUrl"/> mirrors the manifest-configured URL at parse time.
    /// </summary>

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Scopes absent from the GitHub Docs table that GitHub Actions still accepts in a workflow.
    /// Keeping them known avoids reporting "unknown permission scope" for workflows that still declare them.
    /// </summary>
    private static readonly (string Name, string[] Allowed, string Reason, string? DeprecationNote)[] CompatScopes =
    [
        ("repository-projects", ["read", "write", "none"], "actionlint compatibility", null),
        // GitHub Models was retired on 2026-07-30 and the docs table dropped the scope,
        // but workflows declaring it still run without error.
        ("models", ["read", "none"], "retired scope compatibility",
            "GitHub Models is retired and the scope has no effect. remove it from permissions: https://github.blog/changelog/2026-07-30-github-models-is-now-retired/"),
    ];

    public async Task<SourceManifestEntry> FetchAsync(string repoRoot)
    {
        await FetchSourceFilesAsync(repoRoot);
        ParseLocalSourceFiles(repoRoot);
        MergeParsedSources(repoRoot);

        var paths = Paths(repoRoot);
        var rawHash = SourceContentHasher.ComputeSha256(File.ReadAllText(paths.RawDocsPath));
        var sourceUrls = ManifestSourceUrls.Resolve(repoRoot, "permissions", 1).ToList();

        return new SourceManifestEntry
        {
            Dataset = "permissions",
            SourceUrls = sourceUrls,
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            RawFileHashes = new Dictionary<string, string>
            {
                [Path.GetFileName(paths.RawDocsPath)] = rawHash,
            },
        };
    }

    public async Task FetchSourceFilesAsync(string repoRoot)
    {
        UpdateLogger.Info("[fetch:permissions:sources] downloading official GitHub source files...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var docsUrl = ManifestSourceUrls.ResolveSingle(repoRoot, "permissions");
        var docsContent = await client.GetStringAsync(docsUrl);
        var docsHash = SourceContentHasher.ComputeSha256(docsContent);
        UpdateLogger.Info($"[fetch:permissions:sources] downloaded docs={docsContent.Length} bytes ({docsHash[..16]}...)");

        var paths = Paths(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RawDocsPath)!);

        File.WriteAllText(paths.RawDocsPath, TextNormalization.NormalizeToLf(docsContent));

        UpdateLogger.Info($"[fetch:permissions:sources] wrote {paths.RawDocsPath}");
    }

    public void ParseLocalSourceFiles(string repoRoot)
    {
        var paths = Paths(repoRoot);
        if (!File.Exists(paths.RawDocsPath))
        {
            throw new FileNotFoundException(
                "Permissions raw source files are missing. Run fetch-permissions-sources first.",
                paths.RawDocsPath);
        }

        UpdateLogger.Info("[parse:permissions:sources] parsing local raw source files...");

        var rawText = File.ReadAllText(paths.RawDocsPath);
        var parser = new GitHubDocsPermissionsMarkdownParser();
        var model = parser.Parse(rawText);

        var sourceUrl = ManifestSourceUrls.ResolveSingle(repoRoot, "permissions");

        var snapshot = new ParsedPermissionsSnapshot
        {
            SchemaVersion = 1,
            Source = "github-token-available-permissions-reusable",
            SourceUrl = sourceUrl,
            RawSources = Stage2ArtifactRawSources.FromFiles((paths.RawDocsPath, Path.GetFileName(paths.RawDocsPath))),
            Scopes = model.Scopes.Select(s => new ParsedPermissionsSnapshot.ScopeEntry
            {
                Name = s.Name,
                Allowed = s.Allowed.ToList(),
            }).ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(paths.ParsedPath)!);
        File.WriteAllText(paths.ParsedPath, TextNormalization.NormalizeToLf(JsonSerializer.Serialize(snapshot, JsonOptions)));

        UpdateLogger.Info($"[parse:permissions:sources] wrote {paths.ParsedPath} ({model.Scopes.Count} scopes)");
    }

    public void MergeParsedSources(string repoRoot)
    {
        var paths = Paths(repoRoot);
        if (!File.Exists(paths.ParsedPath))
        {
            throw new FileNotFoundException(
                "Permissions parsed source files are missing. Run parse-permissions-sources first.",
                paths.ParsedPath);
        }

        UpdateLogger.Info("[merge:permissions:sources] merging parsed permissions into canonical snapshot...");

        var parsedText = File.ReadAllText(paths.ParsedPath);
        var parsed = JsonSerializer.Deserialize<ParsedPermissionsSnapshot>(parsedText, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize parsed permissions snapshot");

        // Build the merged model: start from parsed docs data
        var scopes = parsed.Scopes
            .Select(s => new MergedScope { Name = s.Name, Allowed = s.Allowed })
            .ToList();

        // Add scopes the docs table no longer lists but GitHub Actions still accepts
        foreach (var (name, allowed, reason, deprecationNote) in CompatScopes)
        {
            if (!scopes.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal)))
            {
                scopes.Add(new MergedScope { Name = name, Allowed = [.. allowed], DeprecationNote = deprecationNote });
                UpdateLogger.Info($"[merge:permissions:sources] added '{name}' from {reason}");
            }
        }

        // Sort alphabetically
        scopes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        var provenance = !string.IsNullOrWhiteSpace(parsed.SourceUrl)
            ? parsed.SourceUrl.Trim()
            : parsed.Source;

        var merged = new MergedPermissions
        {
            Source = provenance,
            Scopes = scopes,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(paths.MergedPath)!);
        File.WriteAllText(paths.MergedPath, TextNormalization.NormalizeToLf(JsonSerializer.Serialize(merged, JsonOptions)));

        UpdateLogger.Info($"[merge:permissions:sources] wrote {paths.MergedPath} ({scopes.Count} scopes)");
    }

    private static PermissionsPaths Paths(string repoRoot)
    {
        var baseDir = Path.Combine(repoRoot, "data", "sources", "permissions", "github");
        return new PermissionsPaths
        {
            RawDocsPath = Path.Combine(baseDir, "raw", "github-token-available-permissions.md"),
            ParsedPath = Path.Combine(baseDir, "parsed", "permissions-scopes.json"),
            MergedPath = Path.Combine(baseDir, "permissions.json"),
        };
    }

    private sealed class PermissionsPaths
    {
        public string RawDocsPath { get; set; } = string.Empty;
        public string ParsedPath { get; set; } = string.Empty;
        public string MergedPath { get; set; } = string.Empty;
    }

    internal sealed class ParsedPermissionsSnapshot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        /// <summary>HTTPS URL from manifest configuration for this dataset (same contract as Stage 1 fetch).</summary>
        public string? SourceUrl { get; set; }
        public List<RawSourceRef>? RawSources { get; set; }
        public List<ScopeEntry> Scopes { get; set; } = [];

        internal sealed class ScopeEntry
        {
            public string Name { get; set; } = string.Empty;
            public List<string> Allowed { get; set; } = [];
        }
    }

    private sealed class MergedPermissions
    {
        public string Source { get; set; } = string.Empty;
        public List<MergedScope> Scopes { get; set; } = [];
    }

    private sealed class MergedScope
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Allowed { get; set; } = [];

        /// <summary>Set only for retired scopes; omitted from the snapshot for active scopes.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DeprecationNote { get; set; }
    }
}
