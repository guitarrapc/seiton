using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Sources;

internal sealed class GitHubFunctionNamesFetcher
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

        var paths = Paths(repoRoot);
        var docsHash = ComputeSha256(File.ReadAllText(paths.RawDocsPath));
        var sourceUrls = ManifestSourceUrls.Resolve(repoRoot, "function-specs", 1).ToList();

        return new SourceManifestEntry
        {
            Dataset = "function-specs",
            SourceUrls = sourceUrls,
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            RawFileHashes = new Dictionary<string, string>
            {
                [Path.GetFileName(paths.RawDocsPath)] = docsHash,
            },
        };
    }

    public async Task FetchSourceFilesAsync(string repoRoot)
    {
        UpdateLogger.Info("[fetch:function-specs:sources] downloading official GitHub source files...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var docsUrl = ManifestSourceUrls.ResolveSingle(repoRoot, "function-specs");
        var docsContent = await client.GetStringAsync(docsUrl);
        var docsHash = ComputeSha256(docsContent);
        UpdateLogger.Info($"[fetch:function-specs:sources] downloaded docs={docsContent.Length} bytes ({docsHash[..16]}...)");

        var paths = Paths(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RawDocsPath)!);

        File.WriteAllText(paths.RawDocsPath, TextNormalization.NormalizeToLf(docsContent));

        UpdateLogger.Info($"[fetch:function-specs:sources] wrote {paths.RawDocsPath}");
    }

    public void ParseLocalSourceFiles(string repoRoot)
    {
        var paths = Paths(repoRoot);
        if (!File.Exists(paths.RawDocsPath))
        {
            throw new FileNotFoundException(
                "Function-specs raw source files are missing. Run fetch-function-specs-sources first.",
                paths.RawDocsPath);
        }

        UpdateLogger.Info("[parse:function-specs:sources] parsing local raw source files...");

        var docsText = File.ReadAllText(paths.RawDocsPath);
        var parser = new GitHubDocsExpressionsMarkdownParser();
        var names = parser.ParseFunctionNames(docsText);

        var parsed = new ParsedFunctionNamesSnapshot
        {
            SchemaVersion = 1,
            Source = "github-expressions-docs-raw",
            FunctionNames = names.ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(paths.ParsedDocsPath)!);
        File.WriteAllText(paths.ParsedDocsPath, TextNormalization.NormalizeToLf(JsonSerializer.Serialize(parsed, JsonOptions)));

        UpdateLogger.Info($"[parse:function-specs:sources] wrote {paths.ParsedDocsPath} ({names.Count} functions)");
    }

    private static FunctionNamesPaths Paths(string repoRoot)
    {
        var baseDir = Path.Combine(repoRoot, "data", "sources", "function-specs", "github");
        return new FunctionNamesPaths
        {
            RawDocsPath = Path.Combine(baseDir, "raw", "expressions.docs.md"),
            ParsedDocsPath = Path.Combine(baseDir, "parsed", "docs-function-names.json"),
        };
    }

    private static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    private sealed class FunctionNamesPaths
    {
        public string RawDocsPath { get; set; } = string.Empty;
        public string ParsedDocsPath { get; set; } = string.Empty;
    }

    internal sealed class ParsedFunctionNamesSnapshot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<string> FunctionNames { get; set; } = [];
    }
}
