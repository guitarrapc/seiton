using System.Text.Json;
using System.Text.Json.Serialization;
using Seiton.Update.Model;
using Seiton.Update.Services;

namespace Seiton.Update.Sources;

internal sealed class GitHubSuperfluousActionsFetcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions TargetsFileJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<SourceManifestEntry> FetchAsync(string repoRoot)
    {
        await FetchSourceFilesAsync(repoRoot);
        ParseLocalSourceFiles(repoRoot);
        MergeParsedSources(repoRoot);

        var paths = Paths(repoRoot);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(paths.RawDir))
        {
            var fileName = Path.GetFileName(file);
            hashes[fileName] = SourceContentHasher.ComputeSha256(File.ReadAllText(file));
        }

        return new SourceManifestEntry
        {
            Dataset = "superfluous-actions",
            SourceUrls = ["https://api.github.com/repos/{owner}/{repo}"],
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            RawFileHashes = hashes,
        };
    }

    public async Task FetchSourceFilesAsync(string repoRoot)
    {
        UpdateLogger.Info("[fetch:superfluous-actions:sources] checking archive status via GitHub API...");
        var targets = LoadTargets(repoRoot);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.Timeout = TimeSpan.FromSeconds(60);

        // Use GITHUB_TOKEN if available for higher rate limits
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        var paths = Paths(repoRoot);
        Directory.CreateDirectory(paths.RawDir);

        // Clean up stale raw files
        var expectedFileNames = new HashSet<string>(
            targets.Select(static x => $"{x.Owner}_{x.Repo}.json"),
            StringComparer.OrdinalIgnoreCase);

        foreach (var existing in Directory.EnumerateFiles(paths.RawDir))
        {
            var fileName = Path.GetFileName(existing);
            if (!expectedFileNames.Contains(fileName))
            {
                File.Delete(existing);
                UpdateLogger.Info($"[fetch:superfluous-actions:sources] removed stale raw file {fileName}");
            }
        }

        foreach (var target in targets)
        {
            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(target.Owner)}/{Uri.EscapeDataString(target.Repo)}";
            var rawFileName = $"{target.Owner}_{target.Repo}.json";
            var rawPath = Path.Combine(paths.RawDir, rawFileName);

            try
            {
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    // Write a minimal JSON indicating fetch failure (treat as potentially archived/removed)
                    var errorJson = JsonSerializer.Serialize(new
                    {
                        owner = target.Owner,
                        repo = target.Repo,
                        archived = (bool?)null,
                        fetchError = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    }, JsonOptions).Replace("\r\n", "\n");

                    File.WriteAllText(rawPath, errorJson);
                    UpdateLogger.Info($"[fetch:superfluous-actions:sources] {target.Owner}/{target.Repo}: HTTP {(int)response.StatusCode} (wrote error marker)");
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync();

                // Extract only the fields we need (archived, full_name)
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                var archived = root.TryGetProperty("archived", out var archivedProp) && archivedProp.GetBoolean();
                var fullName = root.TryGetProperty("full_name", out var nameProp) ? nameProp.GetString() : $"{target.Owner}/{target.Repo}";

                var extracted = JsonSerializer.Serialize(new
                {
                    owner = target.Owner,
                    repo = target.Repo,
                    fullName,
                    archived,
                    fetchError = (string?)null,
                }, JsonOptions).Replace("\r\n", "\n");

                File.WriteAllText(rawPath, extracted);
                var hash = SourceContentHasher.ComputeSha256(extracted);
                UpdateLogger.Info($"[fetch:superfluous-actions:sources] {target.Owner}/{target.Repo}: archived={archived} ({hash[..16]}...)");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                var errorJson = JsonSerializer.Serialize(new
                {
                    owner = target.Owner,
                    repo = target.Repo,
                    archived = (bool?)null,
                    fetchError = ex.Message,
                }, JsonOptions).Replace("\r\n", "\n");

                File.WriteAllText(rawPath, errorJson);
                UpdateLogger.Info($"[fetch:superfluous-actions:sources] {target.Owner}/{target.Repo}: error ({ex.Message})");
            }
        }
    }

    public void ParseLocalSourceFiles(string repoRoot)
    {
        var paths = Paths(repoRoot);
        var targets = LoadTargets(repoRoot);

        UpdateLogger.Info("[parse:superfluous-actions:sources] parsing raw API responses...");

        var parsed = new ParsedSuperfluousActionsSnapshot
        {
            SchemaVersion = 1,
            Source = "github-api-repos",
            Actions = [],
        };

        foreach (var target in targets)
        {
            var rawFileName = $"{target.Owner}_{target.Repo}.json";
            var rawPath = Path.Combine(paths.RawDir, rawFileName);

            if (!File.Exists(rawPath))
            {
                throw new FileNotFoundException(
                    "Superfluous-actions raw source files are missing. Run fetch-superfluous-actions-sources first.",
                    rawPath);
            }

            var text = File.ReadAllText(rawPath);
            var rawEntry = JsonSerializer.Deserialize<RawRepoEntry>(text, TargetsFileJsonOptions);

            parsed.Actions.Add(new ParsedSuperfluousAction
            {
                Owner = target.Owner,
                Repo = target.Repo,
                Archived = rawEntry?.Archived ?? false,
                FetchError = rawEntry?.FetchError,
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(paths.ParsedPath)!);
        File.WriteAllText(paths.ParsedPath, JsonSerializer.Serialize(parsed, JsonOptions).Replace("\r\n", "\n"));
        UpdateLogger.Info($"[parse:superfluous-actions:sources] wrote {paths.ParsedPath}");
    }

    public void MergeParsedSources(string repoRoot)
    {
        var paths = Paths(repoRoot);
        var targets = LoadTargets(repoRoot);

        if (!File.Exists(paths.ParsedPath))
        {
            throw new FileNotFoundException(
                "Superfluous-actions parsed file is missing. Run parse-superfluous-actions-sources first.",
                paths.ParsedPath);
        }

        UpdateLogger.Info("[merge:superfluous-actions:sources] merging parsed sources...");

        var parsedText = File.ReadAllText(paths.ParsedPath);
        var parsed = JsonSerializer.Deserialize<ParsedSuperfluousActionsSnapshot>(parsedText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Invalid parsed superfluous-actions snapshot: {paths.ParsedPath}");

        // Build archive status lookup
        var archiveStatus = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in parsed.Actions)
        {
            archiveStatus[$"{action.Owner}/{action.Repo}"] = action.Archived;
        }

        // Filter out archived repos and merge with target metadata (replacement, description)
        var snapshot = new
        {
            schemaVersion = 1,
            source = "github-api-repos-merged",
            actions = targets
                .Where(t =>
                {
                    var key = $"{t.Owner}/{t.Repo}";
                    if (archiveStatus.TryGetValue(key, out var archived) && archived)
                    {
                        UpdateLogger.Info($"[merge:superfluous-actions:sources] excluding archived repo: {key}");
                        return false;
                    }
                    return true;
                })
                .OrderBy(static x => x.Owner, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static x => x.Repo, StringComparer.OrdinalIgnoreCase)
                .Select(t => new
                {
                    owner = t.Owner.ToLowerInvariant(),
                    repo = t.Repo.ToLowerInvariant(),
                    replacement = t.Replacement,
                    description = t.Description,
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
            UpdateLogger.Info($"[merge:superfluous-actions:sources] updated {paths.MergedPath}");
        }
        else
        {
            UpdateLogger.Info("[merge:superfluous-actions:sources] snapshot already up to date.");
        }
    }

    private static SuperfluousActionsPaths Paths(string repoRoot)
    {
        var baseDir = Path.Combine(repoRoot, "data", "sources", "superfluous-actions", "github");
        return new SuperfluousActionsPaths
        {
            RawDir = Path.Combine(baseDir, "raw"),
            ParsedPath = Path.Combine(baseDir, "parsed", "superfluous-actions-repos.json"),
            MergedPath = Path.Combine(baseDir, "superfluous_actions.json"),
        };
    }

    private static IReadOnlyList<SuperfluousActionsTarget> LoadTargets(string repoRoot)
    {
        var configPath = Path.Combine(repoRoot, "data", "sources", "superfluous-actions", "targets.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "Superfluous-actions target config is missing. Expected data/sources/superfluous-actions/targets.json.",
                configPath);
        }

        var configText = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<SuperfluousActionsTargetsFile>(configText, TargetsFileJsonOptions)
            ?? throw new InvalidDataException($"Invalid superfluous-actions target config: {configPath}");

        if (config.Targets is null || config.Targets.Count == 0)
        {
            throw new InvalidDataException($"Superfluous-actions target config has no targets: {configPath}");
        }

        return config.Targets
            .Where(static x => !string.IsNullOrWhiteSpace(x.Owner) && !string.IsNullOrWhiteSpace(x.Repo) && !string.IsNullOrWhiteSpace(x.Replacement))
            .OrderBy(static x => x.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static x => x.Repo, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class SuperfluousActionsTargetsFile
    {
        public int SchemaVersion { get; set; }
        public string? Description { get; set; }
        public List<SuperfluousActionsTarget>? Targets { get; set; }
    }

    private sealed class SuperfluousActionsTarget
    {
        public string Owner { get; set; } = string.Empty;
        public string Repo { get; set; } = string.Empty;
        public string Replacement { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    private sealed class SuperfluousActionsPaths
    {
        public string RawDir { get; set; } = string.Empty;
        public string ParsedPath { get; set; } = string.Empty;
        public string MergedPath { get; set; } = string.Empty;
    }

    private sealed class RawRepoEntry
    {
        public string? Owner { get; set; }
        public string? Repo { get; set; }
        public string? FullName { get; set; }
        public bool Archived { get; set; }
        public string? FetchError { get; set; }
    }

    private sealed class ParsedSuperfluousActionsSnapshot
    {
        public int SchemaVersion { get; set; }
        public string? Source { get; set; }
        public List<ParsedSuperfluousAction> Actions { get; set; } = [];
    }

    private sealed class ParsedSuperfluousAction
    {
        public string Owner { get; set; } = string.Empty;
        public string Repo { get; set; } = string.Empty;
        public bool Archived { get; set; }
        public string? FetchError { get; set; }
    }
}
