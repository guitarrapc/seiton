using System.Text.Json;
using System.Text.Json.Serialization;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Sources;

internal sealed class GitHubPopularActionsFetcher
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
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
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
            hashes[source.RawFileName] = SourceContentHasher.ComputeSha256(File.ReadAllText(path));
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

        // Clean up stale raw files that are no longer referenced by targets.json
        var expectedFileNames = new HashSet<string>(
            sources.Select(static x => x.RawFileName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var existing in Directory.EnumerateFiles(paths.RawDir))
        {
            var fileName = Path.GetFileName(existing);
            if (!expectedFileNames.Contains(fileName))
            {
                File.Delete(existing);
                UpdateLogger.Info($"[fetch:popular-actions:sources] removed stale raw file {fileName}");
            }
        }

        foreach (var source in sources)
        {
            var content = await client.GetStringAsync(source.Url);
            var hash = SourceContentHasher.ComputeSha256(content);
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
                Inputs = inputs.Select(static x => new ParsedPopularActionInput { Name = x.Name, Required = x.Required, DeprecationMessage = x.DeprecationMessage }).ToList(),
                Outputs = outputs.Select(static x => new ParsedPopularActionOutput { Name = x.Name }).ToList(),
                RunsUsing = runsUsing,
            });
        }

        parsed.Actions = parsed.Actions
            .OrderBy(static x => x.Uses, StringComparer.Ordinal)
            .ToList();

        parsed.RawSources = Stage2ArtifactRawSources.FromFiles(
            sources.Select(s => (Path.Combine(paths.RawDir, s.RawFileName!), s.RawFileName!)).ToArray());

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
        var sources = LoadSources(repoRoot);

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

        // Build lookup from targets.json for maxDeprecatedMajorVersion
        var deprecatedVersionLookup = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.MaxDeprecatedMajorVersion > 0)
            {
                deprecatedVersionLookup[source.Uses] = source.MaxDeprecatedMajorVersion;
            }
        }

        // Load supplemental required permissions
        var supplementalPermissionsPath = Path.Combine(
            repoRoot, "data", "sources", "popular-actions", "supplemental-required-permissions.json");
        var requiredPermissionsLookup = new Dictionary<string, SupplementalPermissionEntry[]>(StringComparer.Ordinal);
        if (File.Exists(supplementalPermissionsPath))
        {
            var supText = File.ReadAllText(supplementalPermissionsPath);
            var supData = JsonSerializer.Deserialize<SupplementalRequiredPermissionsFile>(supText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (supData?.Actions is not null)
            {
                foreach (var entry in supData.Actions)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Uses) && entry.RequiredPermissions is { Count: > 0 })
                    {
                        requiredPermissionsLookup[entry.Uses] = entry.RequiredPermissions.ToArray();
                    }
                }
            }
        }

        var snapshot = new
        {
            schemaVersion = 1,
            source = "github-official-merged-snapshot",
            actions = parsed.Actions
                .OrderBy(static x => x.Uses, StringComparer.Ordinal)
                .Select(x => new
                {
                    uses = x.Uses,
                    inputs = x.Inputs
                        .Where(static n => !string.IsNullOrWhiteSpace(n.Name))
                        .DistinctBy(static n => n.Name, StringComparer.Ordinal)
                        .OrderBy(static n => n.Name, StringComparer.Ordinal)
                        .Select(static n => new { name = n.Name, required = n.Required, deprecationMessage = n.DeprecationMessage })
                        .ToArray(),
                    outputs = (x.Outputs ?? [])
                        .Where(static n => !string.IsNullOrWhiteSpace(n.Name))
                        .DistinctBy(static n => n.Name, StringComparer.Ordinal)
                        .OrderBy(static n => n.Name, StringComparer.Ordinal)
                        .Select(static n => new { name = n.Name })
                        .ToArray(),
                    runsUsing = x.RunsUsing ?? string.Empty,
                    maxDeprecatedMajorVersion = deprecatedVersionLookup.GetValueOrDefault(x.Uses, 0),
                    requiredPermissions = requiredPermissionsLookup.TryGetValue(x.Uses, out var perms)
                        ? perms.Select(static p => new { scope = p.Scope, access = p.Access }).ToArray()
                        : Array.Empty<object>(),
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
        PopularActionsTargetsFile config;
        try
        {
            config = JsonSerializer.Deserialize<PopularActionsTargetsFile>(configText, TargetsFileJsonOptions)
                ?? throw new InvalidDataException($"Invalid popular-actions target config: {configPath}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Invalid popular-actions target config: {configPath}. Remove unknown properties (e.g. obsolete \"url\"); URLs are defined in manifest only. {ex.Message}",
                ex);
        }

        var sources = (config.Targets ?? [])
            .Select(static x => new PopularActionSource
            {
                ActionRef = (x.ActionRef ?? string.Empty).Trim(),
                Uses = (x.Uses ?? string.Empty).Trim(),
                Url = string.Empty,
                RawFileName = (x.RawFileName ?? string.Empty).Trim(),
                MaxDeprecatedMajorVersion = x.MaxDeprecatedMajorVersion,
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
                throw new InvalidDataException($"Popular-actions target config {entryName}.actionRef is required (owner/repo@ref, e.g. actions/checkout@v6).");
            }

            var atIdx = source.ActionRef.LastIndexOf('@');
            if (atIdx <= 0 || atIdx == source.ActionRef.Length - 1)
            {
                throw new InvalidDataException($"Popular-actions target config {entryName}.actionRef must include a non-empty ref after '@'.");
            }

            var actionRefPrefix = source.ActionRef[..atIdx].Trim();
            if (!string.Equals(actionRefPrefix, source.Uses, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Popular-actions target config {entryName}.actionRef must start with the same owner/repo as uses (expected '{source.Uses}@…', got '{source.ActionRef}').");
            }
        }

        sources = sources
            .OrderBy(static x => x.Uses, StringComparer.Ordinal)
            .ToList();

        var manifestUrls = ManifestSourceUrls.Resolve(repoRoot, "popular-actions", expectedCount: null);
        if (manifestUrls.Count != sources.Count)
        {
            throw new InvalidDataException(
                $"popular-actions: manifest.json lists {manifestUrls.Count} sourceUrls but targets.json has {sources.Count} targets. " +
                "Ensure each target has a matching URL in manifest sourceUrls in uses-ascending order.");
        }

        for (var i = 0; i < sources.Count; i++)
        {
            EnsurePopularActionUrlMatchesTarget(sources[i], manifestUrls[i], i);
            sources[i].Url = manifestUrls[i];
        }

        return sources;
    }

    /// <summary>
    /// Ensures the manifest URL points at raw.githubusercontent.com under the same owner/repo/ref as
    /// <see cref="PopularActionSource.ActionRef"/>, so URL edits cannot fetch a different version or subtree.
    /// </summary>
    private static void EnsurePopularActionUrlMatchesTarget(PopularActionSource source, string url, int index)
    {
        var uses = source.Uses;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"popular-actions: manifest sourceUrls[{index}] must be an absolute https URL on raw.githubusercontent.com for uses '{uses}'. url={url}");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
        {
            throw new InvalidDataException(
                $"popular-actions: manifest sourceUrls[{index}] path must be /owner/repo/ref/.../action metadata. uses='{uses}' url={url}");
        }

        var ownerEnd = uses.IndexOf('/');
        if (ownerEnd <= 0 || ownerEnd == uses.Length - 1 || uses.IndexOf('/', ownerEnd + 1) >= 0)
        {
            throw new InvalidDataException($"popular-actions: targets uses '{uses}' must be exactly owner/repo.");
        }

        var owner = uses[..ownerEnd];
        var repo = uses[(ownerEnd + 1)..];
        if (!string.Equals(segments[0], owner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], repo, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"popular-actions: manifest sourceUrls[{index}] path starts with '{segments[0]}/{segments[1]}' but targets[{index}] uses '{uses}' after sorting. Re-order or fix manifest URLs to match uses order.");
        }

        var file = segments[^1];
        if (!file.Equals("action.yml", StringComparison.OrdinalIgnoreCase) &&
            !file.Equals("action.yaml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"popular-actions: manifest sourceUrls[{index}] must reference action.yml or action.yaml. url={url}");
        }

        var atIdx = source.ActionRef.LastIndexOf('@');
        var refFromTarget = source.ActionRef[(atIdx + 1)..].Trim();
        var pathRef = string.Join("/", segments[2..^1]);
        if (!string.Equals(pathRef, refFromTarget, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"popular-actions: manifest sourceUrls[{index}] path ref is '{pathRef}' but targets actionRef expects ref '{refFromTarget}' (full '{source.ActionRef}'). url={url}");
        }
    }

    private sealed class PopularActionSource
    {
        public string ActionRef { get; set; } = string.Empty;
        public string Uses { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string RawFileName { get; set; } = string.Empty;
        public int MaxDeprecatedMajorVersion { get; set; }
    }

    /// <summary>targets.json only — download URLs must not appear here (manifest only).</summary>
    private sealed class PopularActionsTargetsFile
    {
        [JsonPropertyName("$schema")]
        public string? JsonSchema { get; set; }

        public int SchemaVersion { get; set; } = 1;
        public List<PopularTargetFileEntry>? Targets { get; set; }
    }

    private sealed class PopularTargetFileEntry
    {
        public string? ActionRef { get; set; }
        public string? Uses { get; set; }
        public string? RawFileName { get; set; }
        public int MaxDeprecatedMajorVersion { get; set; }
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
        public List<RawSourceRef>? RawSources { get; set; }
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
        public string? DeprecationMessage { get; set; }
    }

    private sealed class ParsedPopularActionOutput
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class SupplementalRequiredPermissionsFile
    {
        public List<SupplementalPermissionAction>? Actions { get; set; }
    }

    private sealed class SupplementalPermissionAction
    {
        public string Uses { get; set; } = string.Empty;
        public List<SupplementalPermissionEntry>? RequiredPermissions { get; set; }
    }

    private sealed class SupplementalPermissionEntry
    {
        public string Scope { get; set; } = string.Empty;
        public string Access { get; set; } = string.Empty;
    }
}
