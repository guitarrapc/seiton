using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;

namespace Seiton.Update.Sources;

internal sealed class GitHubContextTypesFetcher
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

        var paths = Paths(repoRoot);
        var docsHash = ComputeSha256(File.ReadAllText(paths.RawDocsPath));

        return new SourceManifestEntry
        {
            Dataset = "context-types",
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
        UpdateLogger.Info("[fetch:context-types:sources] downloading official GitHub source files...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var docsContent = await client.GetStringAsync(DocsSourceUrl);
        var docsHash = ComputeSha256(docsContent);
        UpdateLogger.Info($"[fetch:context-types:sources] downloaded docs={docsContent.Length} bytes ({docsHash[..16]}...)");

        var paths = Paths(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RawDocsPath)!);

        File.WriteAllText(paths.RawDocsPath, TextNormalization.NormalizeToLf(docsContent));

        UpdateLogger.Info($"[fetch:context-types:sources] wrote {paths.RawDocsPath}");
    }

    public void ParseLocalSourceFiles(string repoRoot)
    {
        var paths = Paths(repoRoot);
        if (!File.Exists(paths.RawDocsPath))
        {
            throw new FileNotFoundException(
                "Context-types raw source files are missing. Run fetch-context-types-sources first.",
                paths.RawDocsPath);
        }

        UpdateLogger.Info("[parse:context-types:sources] parsing local raw source files...");

        var docsText = File.ReadAllText(paths.RawDocsPath);
        var parser = new GitHubDocsContextTypesMarkdownParser();
        var contexts = parser.ParseContextProperties(docsText);

        var snapshot = new ParsedContextTypesSnapshot
        {
            SchemaVersion = 1,
            Source = "github-contexts-docs-raw",
            Contexts = contexts.Select(static c => new ParsedContextEntry
            {
                Name = c.Name,
                Properties = c.Properties
                    .Select(static p => new ParsedPropertyPath { Path = p.DotPath, Type = p.Type })
                    .ToList(),
            }).ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(paths.ParsedDocsPath)!);
        File.WriteAllText(paths.ParsedDocsPath, TextNormalization.NormalizeToLf(JsonSerializer.Serialize(snapshot, JsonOptions)));

        UpdateLogger.Info($"[parse:context-types:sources] wrote {paths.ParsedDocsPath} ({contexts.Count} contexts)");
    }

    internal static ContextTypesPaths Paths(string repoRoot)
    {
        var baseDir = Path.Combine(repoRoot, "data", "sources", "context-types", "github");
        return new ContextTypesPaths
        {
            RawDocsPath = Path.Combine(baseDir, "raw", "contexts.docs.md"),
            ParsedDocsPath = Path.Combine(baseDir, "parsed", "docs-contexts.json"),
        };
    }

    private static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    internal sealed class ContextTypesPaths
    {
        public string RawDocsPath { get; set; } = string.Empty;
        public string ParsedDocsPath { get; set; } = string.Empty;
    }

    internal sealed class ParsedContextTypesSnapshot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<ParsedContextEntry> Contexts { get; set; } = [];
    }

    internal sealed class ParsedContextEntry
    {
        public string Name { get; set; } = string.Empty;
        public List<ParsedPropertyPath> Properties { get; set; } = [];
    }

    internal sealed class ParsedPropertyPath
    {
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }
}
