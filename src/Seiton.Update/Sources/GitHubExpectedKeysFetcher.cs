using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;

namespace Seiton.Update.Sources;

internal sealed class GitHubExpectedKeysFetcher
{
    private const string DocsSourceUrl = "https://raw.githubusercontent.com/github/docs/main/content/actions/reference/workflows-and-actions/workflow-syntax.md";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<SourceManifestEntry> FetchAsync(string repoRoot)
    {
        await FetchSourceFilesAsync(repoRoot);
        ParseLocalSourceFiles(repoRoot);

        var rawDir = Services.ExpectedKeysSourcePathResolver.ResolveRawDir(repoRoot);
        var rawPath = Path.Combine(rawDir, "workflow-syntax.md");
        var docsHash = ComputeSha256(File.ReadAllText(rawPath));

        return new SourceManifestEntry
        {
            Dataset = "expected-keys",
            SourceUrls = [DocsSourceUrl],
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

        var docsContent = await client.GetStringAsync(DocsSourceUrl);
        var docsHash = ComputeSha256(docsContent);
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
            Sections = model.Sections.Select(static s => new ExpectedKeysSnapshotSection
            {
                Name = s.Name,
                Description = s.Description,
                Keys = s.Keys.ToList(),
            }).ToList(),
        };

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var primaryDir = Services.ExpectedKeysSourcePathResolver.ResolvePrimaryDir(repoRoot);
        Directory.CreateDirectory(primaryDir);

        var outputPath = Path.Combine(primaryDir, "expected-keys.json");
        File.WriteAllText(outputPath, TextNormalization.NormalizeToLf(json + "\n"));

        UpdateLogger.Info($"[parse:expected-keys:sources] wrote {outputPath} ({model.Sections.Count} sections)");
    }

    private static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    private sealed class ExpectedKeysSnapshot
    {
        public List<ExpectedKeysSnapshotSection>? Sections { get; set; }
    }

    private sealed class ExpectedKeysSnapshotSection
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? Keys { get; set; }
    }
}
