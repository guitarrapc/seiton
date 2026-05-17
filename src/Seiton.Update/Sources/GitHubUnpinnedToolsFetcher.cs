using System.Text.Json;
using System.Text.Json.Serialization;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Sources;

internal sealed class GitHubUnpinnedToolsFetcher
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
            Dataset = "unpinned-tools",
            SourceUrls = sources.Select(static x => x.Url).ToList(),
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            RawFileHashes = hashes,
        };
    }

    public async Task FetchSourceFilesAsync(string repoRoot)
    {
        UpdateLogger.Info("[fetch:unpinned-tools:sources] downloading action metadata files...");
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
                UpdateLogger.Info($"[fetch:unpinned-tools:sources] removed stale raw file {fileName}");
            }
        }

        foreach (var source in sources)
        {
            var content = await client.GetStringAsync(source.Url);
            var hash = SourceContentHasher.ComputeSha256(content);
            var rawPath = Path.Combine(paths.RawDir, source.RawFileName);

            File.WriteAllText(rawPath, content.Replace("\r\n", "\n"));
            UpdateLogger.Info($"[fetch:unpinned-tools:sources] wrote {rawPath} ({hash[..16]}...)");
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
                    "Unpinned-tools raw source files are missing. Run fetch-unpinned-tools-sources first.",
                    rawPath);
            }
        }

        UpdateLogger.Info("[parse:unpinned-tools:sources] parsing local raw source files...");

        var yamlParser = new GitHubActionMetadataYamlParser();
        var parsed = new ParsedUnpinnedToolsSnapshot
        {
            SchemaVersion = 1,
            Source = "github-action-metadata-raw",
            Actions = [],
        };

        foreach (var source in sources)
        {
            var rawPath = Path.Combine(paths.RawDir, source.RawFileName);
            var text = File.ReadAllText(rawPath);
            var inputNames = yamlParser.ParseInputNames(text);

            // Validate the declared versionInput actually exists in the action's inputs
            if (!inputNames.Contains(source.VersionInput, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Unpinned-tools target '{source.Uses}' declares versionInput='{source.VersionInput}' " +
                    $"but the fetched action.yml does not have that input. " +
                    $"Available inputs: [{string.Join(", ", inputNames)}]. " +
                    $"The action may have renamed or removed the input. Update targets.json accordingly.");
            }

            // Find the exact case of the version input as declared in action.yml
            var actualInputName = inputNames.First(n => string.Equals(n, source.VersionInput, StringComparison.OrdinalIgnoreCase));

            parsed.Actions.Add(new ParsedUnpinnedToolAction
            {
                Uses = source.Uses,
                VersionInput = actualInputName,
                AllInputs = inputNames.OrderBy(static x => x, StringComparer.Ordinal).ToList(),
            });
        }

        parsed.Actions = parsed.Actions
            .OrderBy(static x => x.Uses, StringComparer.Ordinal)
            .ToList();

        parsed.RawSources = Stage2ArtifactRawSources.FromFiles(
            sources.Select(s => (Path.Combine(paths.RawDir, s.RawFileName!), s.RawFileName!)).ToArray());

        Directory.CreateDirectory(Path.GetDirectoryName(paths.ParsedPath)!);
        File.WriteAllText(paths.ParsedPath, JsonSerializer.Serialize(parsed, JsonOptions).Replace("\r\n", "\n"));

        UpdateLogger.Info($"[parse:unpinned-tools:sources] wrote {paths.ParsedPath}");
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
                "Unpinned-tools parsed source files are missing. Run parse-unpinned-tools-sources first.",
                paths.ParsedPath);
        }

        UpdateLogger.Info("[merge:unpinned-tools:sources] merging parsed sources...");

        var parsedText = File.ReadAllText(paths.ParsedPath);
        var parsed = JsonSerializer.Deserialize<ParsedUnpinnedToolsSnapshot>(parsedText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Invalid parsed unpinned-tools snapshot: {paths.ParsedPath}");

        // Build lookup from targets.json for description (human-provided metadata)
        var targetsPath = Path.Combine(repoRoot, "data", "sources", "unpinned-tools", "targets.json");
        var targetsText = File.ReadAllText(targetsPath);
        var targetsFile = JsonSerializer.Deserialize<UnpinnedToolsTargetsFile>(targetsText, TargetsFileJsonOptions)
            ?? throw new InvalidDataException($"Invalid targets file: {targetsPath}");
        var descriptionLookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in targetsFile.Targets ?? [])
        {
            if (!string.IsNullOrWhiteSpace(target.Uses) && !string.IsNullOrWhiteSpace(target.Description))
            {
                descriptionLookup[target.Uses] = target.Description!;
            }
        }

        var snapshot = new
        {
            schemaVersion = 1,
            source = "github-action-metadata-merged",
            actions = parsed.Actions
                .OrderBy(static x => x.Uses, StringComparer.Ordinal)
                .Select(x =>
                {
                    var parts = x.Uses.Split('/');
                    var owner = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
                    var repo = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;
                    return new
                    {
                        owner,
                        repo,
                        versionInput = x.VersionInput,
                        description = descriptionLookup.GetValueOrDefault(x.Uses, string.Empty),
                    };
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
            UpdateLogger.Info($"[merge:unpinned-tools:sources] updated {paths.MergedPath}");
        }
        else
        {
            UpdateLogger.Info("[merge:unpinned-tools:sources] snapshot already up to date.");
        }
    }

    private static UnpinnedToolsPaths Paths(string repoRoot)
    {
        var baseDir = Path.Combine(repoRoot, "data", "sources", "unpinned-tools", "github");
        return new UnpinnedToolsPaths
        {
            RawDir = Path.Combine(baseDir, "raw"),
            ParsedPath = Path.Combine(baseDir, "parsed", "unpinned-tools-metadata.json"),
            MergedPath = Path.Combine(baseDir, "unpinned_tools.json"),
        };
    }

    private static IReadOnlyList<UnpinnedToolSource> LoadSources(string repoRoot)
    {
        var configPath = Path.Combine(repoRoot, "data", "sources", "unpinned-tools", "targets.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "Unpinned-tools target config is missing. Expected data/sources/unpinned-tools/targets.json.",
                configPath);
        }

        var configText = File.ReadAllText(configPath);
        UnpinnedToolsTargetsFile config;
        try
        {
            config = JsonSerializer.Deserialize<UnpinnedToolsTargetsFile>(configText, TargetsFileJsonOptions)
                ?? throw new InvalidDataException($"Invalid unpinned-tools target config: {configPath}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Invalid unpinned-tools target config: {configPath}. {ex.Message}",
                ex);
        }

        var sources = (config.Targets ?? [])
            .Select(static x => new UnpinnedToolSource
            {
                ActionRef = (x.ActionRef ?? string.Empty).Trim(),
                Uses = (x.Uses ?? string.Empty).Trim(),
                Url = string.Empty,
                RawFileName = (x.RawFileName ?? string.Empty).Trim(),
                VersionInput = (x.VersionInput ?? "version").Trim(),
            })
            .ToList();

        if (sources.Count == 0)
        {
            throw new InvalidDataException($"Unpinned-tools target config has no targets: {configPath}");
        }

        var seenUses = new HashSet<string>(StringComparer.Ordinal);
        var seenRawFileNames = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var entryName = $"targets[{i}]";

            if (string.IsNullOrWhiteSpace(source.Uses))
            {
                throw new InvalidDataException($"Unpinned-tools target config {entryName}.uses is required.");
            }

            if (string.IsNullOrWhiteSpace(source.RawFileName))
            {
                throw new InvalidDataException($"Unpinned-tools target config {entryName}.rawFileName is required.");
            }

            if (source.RawFileName.IndexOfAny(['/', '\\']) >= 0)
            {
                throw new InvalidDataException($"Unpinned-tools target config {entryName}.rawFileName must be a file name only.");
            }

            if (!seenUses.Add(source.Uses))
            {
                throw new InvalidDataException($"Unpinned-tools target config has duplicate uses: {source.Uses}");
            }

            if (!seenRawFileNames.Add(source.RawFileName))
            {
                throw new InvalidDataException($"Unpinned-tools target config has duplicate rawFileName: {source.RawFileName}");
            }

            if (string.IsNullOrWhiteSpace(source.ActionRef))
            {
                throw new InvalidDataException($"Unpinned-tools target config {entryName}.actionRef is required (owner/repo@ref, e.g. aquasecurity/setup-trivy@v0).");
            }

            var atIdx = source.ActionRef.LastIndexOf('@');
            if (atIdx <= 0 || atIdx == source.ActionRef.Length - 1)
            {
                throw new InvalidDataException($"Unpinned-tools target config {entryName}.actionRef must include a non-empty ref after '@'.");
            }

            var actionRefPrefix = source.ActionRef[..atIdx].Trim();
            if (!string.Equals(actionRefPrefix, source.Uses, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unpinned-tools target config {entryName}.actionRef must start with the same owner/repo as uses (expected '{source.Uses}@…', got '{source.ActionRef}').");
            }

            if (string.IsNullOrWhiteSpace(source.VersionInput))
            {
                throw new InvalidDataException($"Unpinned-tools target config {entryName}.versionInput is required.");
            }
        }

        sources = sources
            .OrderBy(static x => x.Uses, StringComparer.Ordinal)
            .ToList();

        var manifestUrls = ManifestSourceUrls.Resolve(repoRoot, "unpinned-tools", expectedCount: null);
        if (manifestUrls.Count != sources.Count)
        {
            throw new InvalidDataException(
                $"unpinned-tools: manifest.json lists {manifestUrls.Count} sourceUrls but targets.json has {sources.Count} targets. " +
                "Ensure each target has a matching URL in manifest sourceUrls in uses-ascending order.");
        }

        for (var i = 0; i < sources.Count; i++)
        {
            EnsureUrlMatchesTarget(sources[i], manifestUrls[i], i);
            sources[i].Url = manifestUrls[i];
        }

        return sources;
    }

    private static void EnsureUrlMatchesTarget(UnpinnedToolSource source, string url, int index)
    {
        var uses = source.Uses;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"unpinned-tools: manifest sourceUrls[{index}] must be an absolute https URL on raw.githubusercontent.com for uses '{uses}'. url={url}");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
        {
            throw new InvalidDataException(
                $"unpinned-tools: manifest sourceUrls[{index}] path must be /owner/repo/ref/.../action metadata. uses='{uses}' url={url}");
        }

        var ownerEnd = uses.IndexOf('/');
        if (ownerEnd <= 0 || ownerEnd == uses.Length - 1 || uses.IndexOf('/', ownerEnd + 1) >= 0)
        {
            throw new InvalidDataException($"unpinned-tools: targets uses '{uses}' must be exactly owner/repo.");
        }

        var owner = uses[..ownerEnd];
        var repo = uses[(ownerEnd + 1)..];
        if (!string.Equals(segments[0], owner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], repo, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"unpinned-tools: manifest sourceUrls[{index}] path starts with '{segments[0]}/{segments[1]}' but targets[{index}] uses '{uses}' after sorting. Re-order or fix manifest URLs to match uses order.");
        }

        var file = segments[^1];
        if (!file.Equals("action.yml", StringComparison.OrdinalIgnoreCase) &&
            !file.Equals("action.yaml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"unpinned-tools: manifest sourceUrls[{index}] must reference action.yml or action.yaml. url={url}");
        }

        var atIdx = source.ActionRef.LastIndexOf('@');
        var refFromTarget = source.ActionRef[(atIdx + 1)..].Trim();
        var pathRef = string.Join("/", segments[2..^1]);
        if (!string.Equals(pathRef, refFromTarget, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"unpinned-tools: manifest sourceUrls[{index}] path ref is '{pathRef}' but targets actionRef expects ref '{refFromTarget}' (full '{source.ActionRef}'). url={url}");
        }
    }

    private sealed class UnpinnedToolSource
    {
        public string ActionRef { get; set; } = string.Empty;
        public string Uses { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string RawFileName { get; set; } = string.Empty;
        public string VersionInput { get; set; } = string.Empty;
    }

    /// <summary>targets.json file shape.</summary>
    private sealed class UnpinnedToolsTargetsFile
    {
        [JsonPropertyName("$schema")]
        public string? JsonSchema { get; set; }

        public int SchemaVersion { get; set; } = 1;
        public List<UnpinnedToolTargetEntry>? Targets { get; set; }
    }

    private sealed class UnpinnedToolTargetEntry
    {
        public string? ActionRef { get; set; }
        public string? Uses { get; set; }
        public string? RawFileName { get; set; }
        public string? VersionInput { get; set; }
        public string? Description { get; set; }
    }

    private sealed class UnpinnedToolsPaths
    {
        public string RawDir { get; set; } = string.Empty;
        public string ParsedPath { get; set; } = string.Empty;
        public string MergedPath { get; set; } = string.Empty;
    }

    private sealed class ParsedUnpinnedToolsSnapshot
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; } = string.Empty;
        public List<RawSourceRef>? RawSources { get; set; }
        public List<ParsedUnpinnedToolAction> Actions { get; set; } = [];
    }

    private sealed class ParsedUnpinnedToolAction
    {
        public string Uses { get; set; } = string.Empty;
        public string VersionInput { get; set; } = string.Empty;
        public List<string> AllInputs { get; set; } = [];
    }
}
