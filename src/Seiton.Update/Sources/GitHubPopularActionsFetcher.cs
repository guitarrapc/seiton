using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;

namespace Seiton.Update.Sources;

internal sealed class GitHubPopularActionsFetcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<SourceManifestEntry> FetchAsync(string repoRoot)
    {
        var sources = LoadSources(repoRoot);
        await FetchSourceFilesAsync(repoRoot);
        ParseLocalSourceFiles(repoRoot);
        MergeParsedSources(repoRoot);

        var paths = Paths(repoRoot);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var path = Path.Combine(paths.RawDir, source.RawFileName);
            hashes[source.RawFileName] = ComputeSha256(File.ReadAllText(path));
        }

        return new SourceManifestEntry
        {
            Dataset = "popular-actions",
            SourceUrls = sources.Select(static x => x.Url).ToList(),
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            RawFileHashes = hashes,
        };
    }

    public async Task FetchSourceFilesAsync(string repoRoot)
    {
        UpdateLogger.Info("[fetch:popular-actions:sources] downloading official GitHub action metadata files...");
        var sources = LoadSources(repoRoot);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var paths = Paths(repoRoot);
        Directory.CreateDirectory(paths.RawDir);

        foreach (var source in sources)
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
        var sources = LoadSources(repoRoot);
        foreach (var source in sources)
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

        foreach (var source in sources)
        {
            var rawPath = Path.Combine(paths.RawDir, source.RawFileName);
            var text = File.ReadAllText(rawPath);
            var inputs = yamlParser.ParseInputs(text);
            var outputs = yamlParser.ParseOutputs(text);
            var runsUsing = yamlParser.ParseRunsUsing(text);

            parsed.Actions.Add(new ParsedPopularAction
            {
                ActionRef = source.ActionRef,
                Uses = source.Uses,
                Inputs = inputs.Select(static x => new ParsedPopularActionInput { Name = x.Name, Required = x.Required }).ToList(),
                Outputs = outputs.Select(static x => new ParsedPopularActionOutput { Name = x.Name }).ToList(),
                RunsUsing = runsUsing,
            });
        }

        parsed.Actions = parsed.Actions
            .OrderBy(static x => x.Uses, StringComparer.Ordinal)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(paths.ParsedPath)!);
        File.WriteAllText(paths.ParsedPath, JsonSerializer.Serialize(parsed, JsonOptions).Replace("\r\n", "\n"));

        UpdateLogger.Info($"[parse:popular-actions:sources] wrote {paths.ParsedPath}");
    }

    public void ValidateTargetsConfig(string repoRoot)
    {
        _ = LoadSources(repoRoot);
    }

    public void MergeParsedSources(string repoRoot)
    {
        _ = LoadSources(repoRoot);

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
                        .Where(static n => !string.IsNullOrWhiteSpace(n.Name))
                        .DistinctBy(static n => n.Name, StringComparer.Ordinal)
                        .OrderBy(static n => n.Name, StringComparer.Ordinal)
                        .Select(static n => new { name = n.Name, required = n.Required })
                        .ToArray(),
                    outputs = (x.Outputs ?? [])
                        .Where(static n => !string.IsNullOrWhiteSpace(n.Name))
                        .DistinctBy(static n => n.Name, StringComparer.Ordinal)
                        .OrderBy(static n => n.Name, StringComparer.Ordinal)
                        .Select(static n => new { name = n.Name })
                        .ToArray(),
                    runsUsing = x.RunsUsing ?? string.Empty,
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

    private static PopularActionsPaths Paths(string repoRoot)
    {
        var baseDir = Path.Combine(repoRoot, "data", "sources", "popular-actions", "github");
        return new PopularActionsPaths
        {
            RawDir = Path.Combine(baseDir, "raw"),
            ParsedPath = Path.Combine(baseDir, "parsed", "popular-actions-metadata.json"),
            MergedPath = Path.Combine(baseDir, "popular_actions.json"),
        };
    }

    private static IReadOnlyList<PopularActionSource> LoadSources(string repoRoot)
    {
        var configPath = Path.Combine(repoRoot, "data", "sources", "popular-actions", "targets.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "Popular-actions target config is missing. Expected data/sources/popular-actions/targets.json.",
                configPath);
        }

        var configText = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<PopularActionsTargetConfig>(configText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Invalid popular-actions target config: {configPath}");

        var sources = (config.Targets ?? [])
            .Select(static x => new PopularActionSource
            {
                ActionRef = (x.ActionRef ?? string.Empty).Trim(),
                Uses = (x.Uses ?? string.Empty).Trim(),
                Url = (x.Url ?? string.Empty).Trim(),
                RawFileName = (x.RawFileName ?? string.Empty).Trim(),
            })
            .ToList();

        if (sources.Count == 0)
        {
            throw new InvalidDataException($"Popular-actions target config has no targets: {configPath}");
        }

        var seenUses = new HashSet<string>(StringComparer.Ordinal);
        var seenRawFileNames = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var entryName = $"targets[{i}]";

            if (string.IsNullOrWhiteSpace(source.Uses))
            {
                throw new InvalidDataException($"Popular-actions target config {entryName}.uses is required.");
            }

            if (string.IsNullOrWhiteSpace(source.Url))
            {
                throw new InvalidDataException($"Popular-actions target config {entryName}.url is required.");
            }

            if (string.IsNullOrWhiteSpace(source.RawFileName))
            {
                throw new InvalidDataException($"Popular-actions target config {entryName}.rawFileName is required.");
            }

            if (source.RawFileName.IndexOfAny(['/', '\\']) >= 0)
            {
                throw new InvalidDataException($"Popular-actions target config {entryName}.rawFileName must be a file name only.");
            }

            if (!seenUses.Add(source.Uses))
            {
                throw new InvalidDataException($"Popular-actions target config has duplicate uses: {source.Uses}");
            }

            if (!seenRawFileNames.Add(source.RawFileName))
            {
                throw new InvalidDataException($"Popular-actions target config has duplicate rawFileName: {source.RawFileName}");
            }

            if (string.IsNullOrWhiteSpace(source.ActionRef))
            {
                source.ActionRef = source.Uses;
            }
        }

        return sources
            .OrderBy(static x => x.Uses, StringComparer.Ordinal)
            .ToList();
    }

    private static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    private sealed class PopularActionSource
    {
        public string ActionRef { get; set; } = string.Empty;
        public string Uses { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string RawFileName { get; set; } = string.Empty;
    }

    private sealed class PopularActionsTargetConfig
    {
        public int SchemaVersion { get; set; } = 1;
        public List<PopularActionSource>? Targets { get; set; }
    }

    private sealed class PopularActionsPaths
    {
        public string RawDir { get; set; } = string.Empty;
        public string ParsedPath { get; set; } = string.Empty;
        public string MergedPath { get; set; } = string.Empty;
    }

    private sealed class ParsedPopularActionsSnapshot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<ParsedPopularAction> Actions { get; set; } = [];
    }

    private sealed class ParsedPopularAction
    {
        public string ActionRef { get; set; } = string.Empty;
        public string Uses { get; set; } = string.Empty;
        public List<ParsedPopularActionInput> Inputs { get; set; } = [];
        public List<ParsedPopularActionOutput>? Outputs { get; set; }
        public string? RunsUsing { get; set; }
    }

    private sealed class ParsedPopularActionInput
    {
        public string Name { get; set; } = string.Empty;
        public bool Required { get; set; }
    }

    private sealed class ParsedPopularActionOutput
    {
        public string Name { get; set; } = string.Empty;
    }
}
