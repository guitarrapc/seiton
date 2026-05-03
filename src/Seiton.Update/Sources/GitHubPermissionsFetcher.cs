using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Sources;

internal sealed class GitHubPermissionsFetcher
{
    /// <summary>
    /// The reusable data file that contains the YAML permissions block with all scopes and allowed values.
    /// Source URL is recorded in data/sources/manifest.json (dataset permissions).
    /// </summary>

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
        var rawHash = ComputeSha256(File.ReadAllText(paths.RawDocsPath));
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
        var docsHash = ComputeSha256(docsContent);
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

        var snapshot = new ParsedPermissionsSnapshot
        {
            SchemaVersion = 1,
            Source = "github-token-available-permissions-reusable",
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

        // Add repository-projects from actionlint compatibility if not present in docs
        if (!scopes.Any(s => string.Equals(s.Name, "repository-projects", StringComparison.Ordinal)))
        {
            scopes.Add(new MergedScope { Name = "repository-projects", Allowed = ["read", "write", "none"] });
            UpdateLogger.Info("[merge:permissions:sources] added 'repository-projects' from actionlint compatibility");
        }

        // Sort alphabetically
        scopes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        var merged = new MergedPermissions
        {
            Source = ManifestSourceUrls.ResolveSingle(repoRoot, "permissions"),
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

    private static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
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
    }
}
