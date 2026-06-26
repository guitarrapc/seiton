using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Sources;

internal sealed class GitHubStepSchemaFetcher
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
        MergeParsedSources(repoRoot);

        var rawDir = StepSchemaSourcePathResolver.ResolveRawDir(repoRoot);
        var rawFileHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fileName in new[] { "github-workflow.schema.json", "workflow-syntax.md" })
        {
            var rawPath = Path.Combine(rawDir, fileName);
            rawFileHashes[fileName] = SourceContentHasher.ComputeSha256(File.ReadAllText(rawPath));
        }

        var sourceUrls = ManifestSourceUrls.Resolve(repoRoot, "step-schema", expectedCount: 2).ToList();

        return new SourceManifestEntry
        {
            Dataset = "step-schema",
            SourceUrls = sourceUrls,
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            RawFileHashes = rawFileHashes,
        };
    }

    public async Task FetchSourceFilesAsync(string repoRoot)
    {
        UpdateLogger.Info("[fetch:step-schema:sources] downloading official source files...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var urls = ManifestSourceUrls.Resolve(repoRoot, "step-schema", expectedCount: 2);
        var schemaUrl = urls[0];
        var docsUrl = urls[1];

        var schemaContent = await client.GetStringAsync(schemaUrl);
        var schemaHash = SourceContentHasher.ComputeSha256(schemaContent);
        UpdateLogger.Info($"[fetch:step-schema:sources] downloaded schema={schemaContent.Length} bytes ({schemaHash[..16]}...)");

        var docsContent = await client.GetStringAsync(docsUrl);
        var docsHash = SourceContentHasher.ComputeSha256(docsContent);
        UpdateLogger.Info($"[fetch:step-schema:sources] downloaded docs={docsContent.Length} bytes ({docsHash[..16]}...)");

        var rawDir = StepSchemaSourcePathResolver.ResolveRawDir(repoRoot);
        Directory.CreateDirectory(rawDir);

        var schemaPath = Path.Combine(rawDir, "github-workflow.schema.json");
        File.WriteAllText(schemaPath, TextNormalization.NormalizeToLf(schemaContent));

        var docsPath = Path.Combine(rawDir, "workflow-syntax.md");
        File.WriteAllText(docsPath, TextNormalization.NormalizeToLf(docsContent));

        UpdateLogger.Info($"[fetch:step-schema:sources] wrote {schemaPath}");
        UpdateLogger.Info($"[fetch:step-schema:sources] wrote {docsPath}");
    }

    public void ParseLocalSourceFiles(string repoRoot)
    {
        var rawDir = StepSchemaSourcePathResolver.ResolveRawDir(repoRoot);
        var schemaPath = Path.Combine(rawDir, "github-workflow.schema.json");
        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException(
                "Step schema raw source files are missing. Run fetch-step-schema-sources first.",
                schemaPath);
        }

        UpdateLogger.Info("[parse:step-schema:sources] parsing local raw source files...");

        var parser = new GitHubWorkflowStepSchemaParser();
        var parsed = parser.ParseFile(schemaPath);
        var model = new StepSchemaParsedModel
        {
            SchemaVersion = parsed.SchemaVersion,
            Source = parsed.Source,
            RawSources = Stage2ArtifactRawSources.FromFiles(
                (schemaPath, "github-workflow.schema.json"),
                (Path.Combine(rawDir, "workflow-syntax.md"), "workflow-syntax.md")),
            Forms = parsed.Forms,
            Properties = parsed.Properties,
            KeyDependencies = parsed.KeyDependencies,
        };

        var snapshot = ToParsedSnapshot(model);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var parsedDir = StepSchemaSourcePathResolver.ResolveParsedDir(repoRoot);
        Directory.CreateDirectory(parsedDir);

        var parsedPath = Path.Combine(parsedDir, "step-schema.json");
        File.WriteAllText(parsedPath, TextNormalization.NormalizeToLf(json + "\n"));

        UpdateLogger.Info($"[parse:step-schema:sources] wrote {parsedPath} ({model.Forms.Count} forms, {model.Properties.Count} properties)");
    }

    public void MergeParsedSources(string repoRoot)
    {
        var parsedPath = StepSchemaSourcePathResolver.ResolveParsed(repoRoot);
        var supplementalPath = StepSchemaSourcePathResolver.ResolveSupplemental(repoRoot);
        if (!File.Exists(supplementalPath))
        {
            throw new FileNotFoundException(
                "Step schema supplemental overlay is missing.",
                supplementalPath);
        }

        UpdateLogger.Info("[merge:step-schema:sources] merging parsed snapshot with supplemental overlay...");

        var sourceParser = new StepSchemaSourceParser();
        var parsed = LoadParsedModel(parsedPath);
        var supplemental = sourceParser.ParseSupplemental(supplementalPath);

        var merged = new StepSchemaMerger().Merge(parsed, supplemental);
        var mergedJson = SerializeCanonical(merged);

        var primaryPath = Path.Combine(StepSchemaSourcePathResolver.ResolveGithubDir(repoRoot), "step-schema.json");
        var existing = File.Exists(primaryPath)
            ? TextNormalization.NormalizeToLf(File.ReadAllText(primaryPath))
            : string.Empty;

        if (!string.Equals(existing, mergedJson, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
            File.WriteAllText(primaryPath, mergedJson);
            UpdateLogger.Info($"[merge:step-schema:sources] wrote {primaryPath}");
        }
        else
        {
            UpdateLogger.Info("[merge:step-schema:sources] canonical snapshot already up to date.");
        }
    }

    private static StepSchemaParsedModel LoadParsedModel(string parsedPath)
    {
        var json = File.ReadAllText(parsedPath);
        var snapshot = JsonSerializer.Deserialize<StepSchemaParsedSnapshot>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Invalid parsed step-schema snapshot: {parsedPath}");

        return new StepSchemaParsedModel
        {
            SchemaVersion = snapshot.SchemaVersion,
            Source = snapshot.Source ?? string.Empty,
            RawSources = snapshot.RawSources ?? [],
            Forms = snapshot.Forms?.Select(static f => new StepSchemaParsedFormModel
            {
                Id = f.Id ?? string.Empty,
                PrimaryKey = f.PrimaryKey ?? string.Empty,
            }).ToList() ?? [],
            Properties = snapshot.Properties?.ToDictionary(
                static p => p.Key,
                static p => new StepSchemaPropertyModel
                {
                    ValueKind = p.Value.ValueKind ?? string.Empty,
                    ExpressionContext = p.Value.ExpressionContext,
                },
                StringComparer.Ordinal) ?? new Dictionary<string, StepSchemaPropertyModel>(StringComparer.Ordinal),
            KeyDependencies = snapshot.KeyDependencies?.Select(static d => new StepSchemaKeyDependencyModel
            {
                Key = d.Key ?? string.Empty,
                RequiresPrimary = d.RequiresPrimary ?? string.Empty,
            }).ToList() ?? [],
        };
    }

    private static StepSchemaParsedSnapshot ToParsedSnapshot(StepSchemaParsedModel model) => new()
    {
        SchemaVersion = model.SchemaVersion,
        Source = model.Source,
        RawSources = model.RawSources.ToList(),
        Forms = model.Forms.Select(static f => new StepSchemaParsedFormSnapshot
        {
            Id = f.Id,
            PrimaryKey = f.PrimaryKey,
        }).ToList(),
        Properties = model.Properties.ToDictionary(
            static p => p.Key,
            static p => new StepSchemaPropertySnapshot
            {
                ValueKind = p.Value.ValueKind,
                ExpressionContext = p.Value.ExpressionContext,
            },
            StringComparer.Ordinal),
        KeyDependencies = model.KeyDependencies.Select(static d => new StepSchemaKeyDependencySnapshot
        {
            Key = d.Key,
            RequiresPrimary = d.RequiresPrimary,
        }).ToList(),
    };

    private static string SerializeCanonical(StepSchemaModel model)
    {
        var snapshot = new StepSchemaCanonicalSnapshot
        {
            SchemaVersion = model.SchemaVersion,
            Source = model.Source,
            RawSources = model.RawSources.ToList(),
            AppliesTo = model.AppliesTo.ToList(),
            SharedKeys = model.SharedKeys.ToList(),
            Forms = model.Forms.Select(static f => new StepSchemaCanonicalFormSnapshot
            {
                Id = f.Id,
                PrimaryKey = f.PrimaryKey,
                UnexpectedKeyDescription = f.UnexpectedKeyDescription,
                AllowedKeys = f.AllowedKeys.ToList(),
                Properties = f.Properties.ToDictionary(
                    static p => p.Key,
                    static p => new StepSchemaPropertySnapshot
                    {
                        ValueKind = p.Value.ValueKind,
                        ExpressionContext = p.Value.ExpressionContext,
                    },
                    StringComparer.Ordinal),
            }).ToList(),
            Modifiers = model.Modifiers.Select(static m => new StepSchemaModifierSnapshot
            {
                Key = m.Key,
                AllowedOnFormIds = m.AllowedOnFormIds.ToList(),
            }).ToList(),
            KeyDependencies = model.KeyDependencies.Select(static d => new StepSchemaKeyDependencySnapshot
            {
                Key = d.Key,
                RequiresPrimary = d.RequiresPrimary,
            }).ToList(),
        };

        return TextNormalization.NormalizeToLf(JsonSerializer.Serialize(snapshot, JsonOptions) + "\n");
    }

    private sealed class StepSchemaParsedSnapshot
    {
        public int SchemaVersion { get; set; }
        public string? Source { get; set; }
        public List<RawSourceRef>? RawSources { get; set; }
        public List<StepSchemaParsedFormSnapshot>? Forms { get; set; }
        public Dictionary<string, StepSchemaPropertySnapshot>? Properties { get; set; }
        public List<StepSchemaKeyDependencySnapshot>? KeyDependencies { get; set; }
    }

    private sealed class StepSchemaParsedFormSnapshot
    {
        public string? Id { get; set; }
        public string? PrimaryKey { get; set; }
    }

    private sealed class StepSchemaCanonicalSnapshot
    {
        public int SchemaVersion { get; set; }
        public string? Source { get; set; }
        public List<RawSourceRef>? RawSources { get; set; }
        public List<string>? AppliesTo { get; set; }
        public List<string>? SharedKeys { get; set; }
        public List<StepSchemaCanonicalFormSnapshot>? Forms { get; set; }
        public List<StepSchemaModifierSnapshot>? Modifiers { get; set; }
        public List<StepSchemaKeyDependencySnapshot>? KeyDependencies { get; set; }
    }

    private sealed class StepSchemaCanonicalFormSnapshot
    {
        public string? Id { get; set; }
        public string? PrimaryKey { get; set; }
        public string? UnexpectedKeyDescription { get; set; }
        public List<string>? AllowedKeys { get; set; }
        public Dictionary<string, StepSchemaPropertySnapshot>? Properties { get; set; }
    }

    private sealed class StepSchemaModifierSnapshot
    {
        public string? Key { get; set; }
        public List<string>? AllowedOnFormIds { get; set; }
    }

    private sealed class StepSchemaKeyDependencySnapshot
    {
        public string? Key { get; set; }
        public string? RequiresPrimary { get; set; }
    }

    private sealed class StepSchemaPropertySnapshot
    {
        public string? ValueKind { get; set; }
        public string? ExpressionContext { get; set; }
    }
}
