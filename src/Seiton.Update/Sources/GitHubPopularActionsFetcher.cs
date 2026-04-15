using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;

namespace Seiton.Update.Sources;

internal sealed class GitHubPopularActionsFetcher
{
    static readonly PopularActionSource[] PopularActionSources =
    [
        new("actions/checkout@v4", "actions/checkout", "https://raw.githubusercontent.com/actions/checkout/v4/action.yml", "actions_checkout_v4.action.yml"),
        new("actions/setup-dotnet@v4", "actions/setup-dotnet", "https://raw.githubusercontent.com/actions/setup-dotnet/v4/action.yml", "actions_setup-dotnet_v4.action.yml"),
        new("actions/setup-node@v4", "actions/setup-node", "https://raw.githubusercontent.com/actions/setup-node/v4/action.yml", "actions_setup-node_v4.action.yml"),
        new("actions/cache@v4", "actions/cache", "https://raw.githubusercontent.com/actions/cache/v4/action.yml", "actions_cache_v4.action.yml"),
        new("actions/upload-artifact@v4", "actions/upload-artifact", "https://raw.githubusercontent.com/actions/upload-artifact/v4/action.yml", "actions_upload-artifact_v4.action.yml"),
        new("actions/download-artifact@v4", "actions/download-artifact", "https://raw.githubusercontent.com/actions/download-artifact/v4/action.yml", "actions_download-artifact_v4.action.yml"),
        new("docker/login-action@v3", "docker/login-action", "https://raw.githubusercontent.com/docker/login-action/v3/action.yml", "docker_login-action_v3.action.yml"),
    ];

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
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in PopularActionSources)
        {
            var path = Path.Combine(paths.RawDir, source.RawFileName);
            hashes[source.RawFileName] = ComputeSha256(File.ReadAllText(path));
        }

        return new SourceManifestEntry
        {
            Dataset = "popular-actions",
            SourceUrls = PopularActionSources.Select(static x => x.Url).ToList(),
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            RawFileHashes = hashes,
        };
    }

    public async Task FetchSourceFilesAsync(string repoRoot)
    {
        UpdateLogger.Info("[fetch:popular-actions:sources] downloading official GitHub action metadata files...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var paths = Paths(repoRoot);
        Directory.CreateDirectory(paths.RawDir);

        foreach (var source in PopularActionSources)
        {
            var content = await client.GetStringAsync(source.Url);
            var hash = ComputeSha256(content);
            var rawPath = Path.Combine(paths.RawDir, source.RawFileName);

            File.WriteAllText(rawPath, content.Replace("\r\n", "\n"));
            UpdateLogger.Info($"[fetch:popular-actions:sources] wrote {rawPath} ({hash[..16]}...)");
        }
    }

    public void ParseLocalSourceFiles(string repoRoot)
    {
        var paths = Paths(repoRoot);
        foreach (var source in PopularActionSources)
        {
            var rawPath = Path.Combine(paths.RawDir, source.RawFileName);
            if (!File.Exists(rawPath))
            {
                throw new FileNotFoundException(
                    "Popular-actions raw source files are missing. Run fetch-popular-actions-sources first.",
                    rawPath);
            }
        }

        UpdateLogger.Info("[parse:popular-actions:sources] parsing local raw source files...");

        var yamlParser = new GitHubActionMetadataYamlParser();
        var parsed = new ParsedPopularActionsSnapshot
        {
            SchemaVersion = 1,
            Source = "github-action-metadata-raw",
            Actions = [],
        };

        foreach (var source in PopularActionSources)
        {
            var rawPath = Path.Combine(paths.RawDir, source.RawFileName);
            var text = File.ReadAllText(rawPath);
            var inputs = yamlParser.ParseInputNames(text);

            parsed.Actions.Add(new ParsedPopularAction
            {
                ActionRef = source.ActionRef,
                Uses = source.Uses,
                Inputs = inputs.ToList(),
            });
        }

        parsed.Actions = parsed.Actions
            .OrderBy(static x => x.Uses, StringComparer.Ordinal)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(paths.ParsedPath)!);
        File.WriteAllText(paths.ParsedPath, JsonSerializer.Serialize(parsed, JsonOptions).Replace("\r\n", "\n"));

        UpdateLogger.Info($"[parse:popular-actions:sources] wrote {paths.ParsedPath}");
    }

    public void MergeParsedSources(string repoRoot)
    {
        var paths = Paths(repoRoot);
        if (!File.Exists(paths.ParsedPath))
        {
            throw new FileNotFoundException(
                "Popular-actions parsed source files are missing. Run parse-popular-actions-sources first.",
                paths.ParsedPath);
        }

        UpdateLogger.Info("[merge:popular-actions:sources] merging parsed sources...");

        var parsedText = File.ReadAllText(paths.ParsedPath);
        var parsed = JsonSerializer.Deserialize<ParsedPopularActionsSnapshot>(parsedText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Invalid parsed popular-actions snapshot: {paths.ParsedPath}");

        var snapshot = new
        {
            schemaVersion = 1,
            source = "github-official-merged-snapshot",
            actions = parsed.Actions
                .OrderBy(static x => x.Uses, StringComparer.Ordinal)
                .Select(static x => new
                {
                    uses = x.Uses,
                    inputs = x.Inputs
                        .Where(static n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(static n => n, StringComparer.Ordinal)
                        .ToArray(),
                })
                .ToArray(),
        };

        var snapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions).Replace("\r\n", "\n");
        var existing = File.Exists(paths.MergedPath)
            ? File.ReadAllText(paths.MergedPath).Replace("\r\n", "\n")
            : string.Empty;

        if (!string.Equals(existing, snapshotJson, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.MergedPath)!);
            File.WriteAllText(paths.MergedPath, snapshotJson);
            UpdateLogger.Info($"[merge:popular-actions:sources] updated {paths.MergedPath}");
        }
        else
        {
            UpdateLogger.Info("[merge:popular-actions:sources] snapshot already up to date.");
        }
    }

    static PopularActionsPaths Paths(string repoRoot)
    {
        var baseDir = Path.Combine(repoRoot, "data", "sources", "popular-actions", "github");
        return new PopularActionsPaths
        {
            RawDir = Path.Combine(baseDir, "raw"),
            ParsedPath = Path.Combine(baseDir, "parsed", "popular-actions-metadata.json"),
            MergedPath = Path.Combine(baseDir, "popular_actions.json"),
        };
    }

    static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    sealed class PopularActionSource
    {
        public PopularActionSource(string actionRef, string uses, string url, string rawFileName)
        {
            ActionRef = actionRef;
            Uses = uses;
            Url = url;
            RawFileName = rawFileName;
        }

        public string ActionRef { get; }
        public string Uses { get; }
        public string Url { get; }
        public string RawFileName { get; }
    }

    sealed class PopularActionsPaths
    {
        public string RawDir { get; set; } = string.Empty;
        public string ParsedPath { get; set; } = string.Empty;
        public string MergedPath { get; set; } = string.Empty;
    }

    sealed class ParsedPopularActionsSnapshot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<ParsedPopularAction> Actions { get; set; } = [];
    }

    sealed class ParsedPopularAction
    {
        public string ActionRef { get; set; } = string.Empty;
        public string Uses { get; set; } = string.Empty;
        public List<string> Inputs { get; set; } = [];
    }
}
