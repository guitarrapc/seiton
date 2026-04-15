using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiton.Update.Model;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Sources;

internal sealed class GitHubWebhookFetcher
{
    const string SchemaSourceUrl = "https://json.schemastore.org/github-workflow.json";
    const string DocsSourceUrl = "https://raw.githubusercontent.com/github/docs/main/content/actions/reference/workflows-and-actions/events-that-trigger-workflows.md";
    const string ParserVersion = "4";

    public async Task<SourceManifestEntry> FetchAsync(string repoRoot, bool excludeSchemaOnly = false)
    {
        UpdateLogger.Info("[fetch:webhooks] fetching official GitHub sources (schema + docs markdown)...");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var schemaContent = await client.GetStringAsync(SchemaSourceUrl);
        var docsContent = await client.GetStringAsync(DocsSourceUrl);
        var schemaHash = ComputeSha256(schemaContent);
        var docsHash = ComputeSha256(docsContent);
        var contentHash = ComputeSha256(schemaContent + "\n---\n" + docsContent);

        UpdateLogger.Info($"[fetch:webhooks] downloaded schema={schemaContent.Length} bytes ({schemaHash[..16]}...), docs={docsContent.Length} bytes ({docsHash[..16]}...)");

        var schemaEvents = ParseSchemaJson(schemaContent);
        var docsParser = new GitHubDocsWebhookMarkdownParser();
        var docsEventNames = docsParser.ParseEventNames(docsContent);
        var docsActivityTypes = docsParser.ParseActivityTypesByEvent(docsContent);
        var events = MergeOfficialSources(schemaEvents, docsEventNames, docsActivityTypes, excludeSchemaOnly);
        UpdateLogger.Info($"[fetch:webhooks] normalized {events.Count} events (schema + docs merge, excludeSchemaOnly={excludeSchemaOnly}).");
        WriteOfficialSourceDiffReport(repoRoot, schemaEvents, docsEventNames, docsActivityTypes, excludeSchemaOnly);

        var snapshotJson = SerializeSnapshot(events);
        var outputPath = Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "webhook_types.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var existing = File.Exists(outputPath)
            ? File.ReadAllText(outputPath).Replace("\r\n", "\n")
            : string.Empty;

        if (!string.Equals(existing, snapshotJson, StringComparison.Ordinal))
        {
            File.WriteAllText(outputPath, snapshotJson);
            UpdateLogger.Info($"[fetch:webhooks] updated {outputPath}");
        }
        else
        {
            UpdateLogger.Info("[fetch:webhooks] snapshot already up to date.");
        }

        return new SourceManifestEntry
        {
            Dataset = "webhooks",
            SourceUrl = SchemaSourceUrl,
            SourceUrls = [SchemaSourceUrl, DocsSourceUrl],
            FetchedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            ParserVersion = ParserVersion,
            ContentHash = contentHash,
        };
    }

    static IReadOnlyList<WebhookEventModel> MergeOfficialSources(
        IReadOnlyList<WebhookEventModel> schemaEvents,
        ISet<string> docsEventNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>?> docsActivityTypes,
        bool excludeSchemaOnly)
    {
        // Start from Docs because Docs are the normative value source for activity types.
        var merged = new Dictionary<string, WebhookEventModel>(StringComparer.Ordinal);
        foreach (var pair in docsActivityTypes)
        {
            merged[pair.Key] = new WebhookEventModel(pair.Key, pair.Value);
        }

        // Apply schema fallback for docs-known events when docs activity-types are unavailable/unparseable.
        foreach (var model in schemaEvents)
        {
            if (merged.ContainsKey(model.Name))
            {
                continue;
            }

            if (docsEventNames.Contains(model.Name))
            {
                merged[model.Name] = model;
            }
        }

        // Include schema-only events by default for compatibility with preview/source lag.
        if (!excludeSchemaOnly)
        {
            foreach (var model in schemaEvents)
            {
                if (!merged.ContainsKey(model.Name))
                {
                    merged[model.Name] = model;
                }
            }
        }

        // Docs-only events that may not have a parseable table row still need to exist.
        foreach (var supplemental in LocalSupplementalEvents)
        {
            if (!merged.ContainsKey(supplemental.Name))
            {
                merged[supplemental.Name] = supplemental;
            }
        }

        return merged.Values
            .OrderBy(static x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    // Parse the SchemaStore github-workflow.json schema.
    // Navigation: properties.on.oneOf -> find the object form -> properties -> event entries.
    // Activity types come from properties.types.items.enum within each event object.
    // Events with no types property => empty list.
    // Events with types property but no items.enum => null (unconstrained, e.g. repository_dispatch).
    static IReadOnlyList<WebhookEventModel> ParseSchemaJson(string schemaJson)
    {
        using var doc = JsonDocument.Parse(schemaJson);
        var root = doc.RootElement;

        var objectForm = FindOnObjectForm(root);
        if (objectForm is null)
        {
            throw new InvalidDataException("Cannot locate the on: object form in the GitHub workflow schema.");
        }

        if (!objectForm.Value.TryGetProperty("properties", out var eventProperties))
        {
            throw new InvalidDataException("Cannot find properties under the on: object form.");
        }

        var events = new List<WebhookEventModel>();
        foreach (var prop in eventProperties.EnumerateObject())
        {
            var name = prop.Name;
            // Skip non-event structural keys
            if (name is "additionalProperties" or "$comment")
            {
                continue;
            }

            var activityTypes = ExtractActivityTypes(root, prop.Value);
            var model = ApplyOverride(new WebhookEventModel(name, activityTypes));
            events.Add(model);
        }

        // Merge Seiton-specific supplemental events (only those not already present)
        var eventNames = new HashSet<string>(events.Select(static e => e.Name), StringComparer.Ordinal);
        foreach (var supplemental in LocalSupplementalEvents)
        {
            if (!eventNames.Contains(supplemental.Name))
            {
                events.Add(supplemental);
            }
        }

        return events.OrderBy(static x => x.Name, StringComparer.Ordinal).ToArray();
    }

    // SchemaStore JSON schema limitations vs. GitHub documentation:
    //
    //   repository_dispatch: The schema uses a generic eventObject ($ref only, no properties override).
    //     The eventObject definition is just {oneOf: [{type:object},{type:null}]} with no types property.
    //     This makes it indistinguishable from events that truly have no types (empty array).
    //     However, repository_dispatch users can define arbitrary types at workflow call time
    //     (https://docs.github.com/en/actions/using-workflows/events-that-trigger-workflows#repository_dispatch).
    //     We override to null (unconstrained) to match the actionlint reference and preserve correct lint behavior.
    //
    //   watch: The schema inherits eventObject with no types override, but GitHub docs define one activity
    //     type: "started". SchemaStore is missing this override. We restore it here to match documentation.
    static readonly Dictionary<string, IReadOnlyList<string>?> SchemaStoreGapOverrides =
        new(StringComparer.Ordinal)
        {
            ["repository_dispatch"] = null,   // user-defined types; JSON schema cannot express this
            ["watch"] = ["started"],           // SchemaStore missing this override; see GitHub docs
        };

    static WebhookEventModel ApplyOverride(WebhookEventModel model)
    {
        if (SchemaStoreGapOverrides.TryGetValue(model.Name, out var overriddenTypes))
        {
            return model with { ActivityTypes = overriddenTypes };
        }

        return model;
    }

    // Official GitHub documentation may include events that are not present in the current
    // SchemaStore workflow schema. These are appended during normalization to keep the
    // snapshot aligned with Seiton's spec contract.
    static readonly WebhookEventModel[] LocalSupplementalEvents =
    [
        new("image_version", []),   // present in GitHub Docs, currently absent from SchemaStore
    ];

    // Find the oneOf branch that is type: "object" and has webhook event properties.
    static JsonElement? FindOnObjectForm(JsonElement root)
    {
        if (!TryNavigate(root, out var onSchema, "properties", "on"))
        {
            return null;
        }

        // Search in oneOf for the object form
        if (onSchema.TryGetProperty("oneOf", out var oneOf))
        {
            foreach (var sub in oneOf.EnumerateArray())
            {
                if (sub.TryGetProperty("type", out var type)
                    && type.GetString() == "object"
                    && sub.TryGetProperty("properties", out _))
                {
                    return sub;
                }
            }
        }

        return null;
    }

    // Extract activity types from an event's JSON schema object.
    // Returns null for unconstrained types, empty list for no-types, or a list of specific types.
    static IReadOnlyList<string>? ExtractActivityTypes(JsonElement root, JsonElement eventSchema)
    {
        // Step 1: find the innermost "properties" element without resolving $ref at the event level.
        //   - Some events co-locate $ref + inline properties (e.g. check_run, issues).
        //     Inline properties override the $ref target; walk inline first.
        //   - pull_request/pull_request_target use oneOf → allOf nesting.
        var properties = FindProperties(root, eventSchema);

        if (properties is null)
        {
            // No inline/combiner properties found; try resolving the top-level $ref.
            if (eventSchema.TryGetProperty("$ref", out var topRef))
            {
                var refPath = topRef.GetString();
                if (refPath?.StartsWith("#/") == true)
                {
                    var target = NavigatePath(root, refPath[2..].Split('/'));
                    if (target.HasValue)
                    {
                        properties = FindProperties(root, target.Value);
                    }
                }
            }
        }

        if (properties is null)
        {
            // No properties section found; event fires without types (e.g. push, fork).
            return [];
        }

        if (!properties.Value.TryGetProperty("types", out var typesProp))
        {
            // Properties section exists but no "types" key; event has no activity types.
            return [];
        }

        // For the types property, prefer inline "items" over resolving its own $ref.
        // e.g. issues: types has { "$ref": "#/definitions/types", "items": { "enum": [...] }, "default" }
        // The inline items.enum overrides the base $ref.
        if (!typesProp.TryGetProperty("items", out var itemsEl))
        {
            // No inline items; try resolving types $ref
            if (typesProp.TryGetProperty("$ref", out var typesRef))
            {
                var refPath = typesRef.GetString();
                if (refPath?.StartsWith("#/") == true)
                {
                    var target = NavigatePath(root, refPath[2..].Split('/'));
                    if (target.HasValue && target.Value.TryGetProperty("items", out itemsEl))
                    {
                        // use items from resolved definition
                    }
                    else
                    {
                        return null; // types exists but no items -> unconstrained
                    }
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null; // types present, no items, no $ref -> unconstrained
            }
        }

        if (itemsEl.TryGetProperty("enum", out var enumValues))
        {
            var list = new List<string>();
            foreach (var value in enumValues.EnumerateArray())
            {
                var s = value.GetString();
                if (s is not null)
                {
                    list.Add(s);
                }
            }

            return list;
        }

        // items exists but no enum -> unconstrained types (user-defined, like repository_dispatch)
        return null;
    }

    // Find the innermost properties object, traversing allOf/oneOf/anyOf WITHOUT resolving
    // top-level $ref (inline properties always win over a $ref sibling).
    static JsonElement? FindProperties(JsonElement root, JsonElement schema)
    {
        // Prefer inline properties at this level (even when $ref is also present as a sibling)
        if (schema.TryGetProperty("properties", out var props))
        {
            return props;
        }

        // Traverse combiners; for pure $ref sub-schemas resolve them before recursing
        foreach (var combiner in new[] { "allOf", "oneOf", "anyOf" })
        {
            if (schema.TryGetProperty(combiner, out var arr))
            {
                foreach (var sub in arr.EnumerateArray())
                {
                    // Only resolve $ref when the sub-schema has no inline keywords of interest
                    var toSearch = sub.TryGetProperty("$ref", out _) && !sub.TryGetProperty("properties", out _)
                        ? ResolveRef(root, sub)
                        : sub;
                    var found = FindProperties(root, toSearch);
                    if (found.HasValue)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    static JsonElement ResolveRef(JsonElement root, JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var refProp))
        {
            var refPath = refProp.GetString();
            if (refPath?.StartsWith("#/") == true)
            {
                var target = NavigatePath(root, refPath[2..].Split('/'));
                if (target.HasValue)
                {
                    return target.Value;
                }
            }
        }

        return schema;
    }

    static JsonElement? NavigatePath(JsonElement root, string[] parts)
    {
        var current = root;
        foreach (var part in parts)
        {
            if (!current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    static bool TryNavigate(JsonElement root, out JsonElement result, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (!current.TryGetProperty(key, out current))
            {
                result = default;
                return false;
            }
        }

        result = current;
        return true;
    }

    static string SerializeSnapshot(IReadOnlyList<WebhookEventModel> events)
    {
        var snapshot = new List<object>();
        foreach (var e in events)
        {
            if (e.ActivityTypes is null)
            {
                snapshot.Add(new { name = e.Name, activityTypes = (object?)null });
            }
            else
            {
                snapshot.Add(new { name = e.Name, activityTypes = e.ActivityTypes });
            }
        }

        var doc = new { schemaVersion = 1, source = "github-official-merged-snapshot", events = snapshot };
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        return json.Replace("\r\n", "\n");
    }

    static void WriteOfficialSourceDiffReport(
        string repoRoot,
        IReadOnlyList<WebhookEventModel> schemaEvents,
        ISet<string> docsEventNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>?> docsActivityTypes,
        bool excludeSchemaOnly)
    {
        var schemaMap = schemaEvents.ToDictionary(static x => x.Name, StringComparer.Ordinal);
        var docsNames = docsEventNames.OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var schemaNames = schemaMap.Keys.OrderBy(static x => x, StringComparer.Ordinal).ToArray();

        var docsOnly = docsNames.Where(x => !schemaMap.ContainsKey(x)).ToArray();
        var schemaOnly = schemaNames.Where(x => !docsActivityTypes.ContainsKey(x)).ToArray();

        var mismatches = new List<(string EventName, IReadOnlyList<string>? Schema, IReadOnlyList<string>? Docs)>();
        foreach (var name in docsNames)
        {
            if (!schemaMap.TryGetValue(name, out var schemaEvent))
            {
                continue;
            }

            if (!docsActivityTypes.TryGetValue(name, out var docsTypes))
            {
                // Docs heading exists but activity-types table is unavailable/unparseable.
                continue;
            }

            if (!AreSameTypes(schemaEvent.ActivityTypes, docsTypes))
            {
                mismatches.Add((name, schemaEvent.ActivityTypes, docsTypes));
            }
        }

        var reportDir = Path.Combine(repoRoot, "data", "sources", "reports");
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, "official-webhooks-source-diff.md");

        var sb = new StringBuilder();
        sb.AppendLine("# Official Source Diff Report: webhooks");
        sb.AppendLine();
        sb.AppendLine("- source-a: https://json.schemastore.org/github-workflow.json");
        sb.AppendLine("- source-b: https://raw.githubusercontent.com/github/docs/main/content/actions/reference/workflows-and-actions/events-that-trigger-workflows.md");
        sb.AppendLine($"- exclude-schema-only: {excludeSchemaOnly}");
        sb.AppendLine($"- generated-at-utc: {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("Policy: normalized snapshot follows GitHub Docs for activity types when Docs table is parseable.");
        sb.AppendLine();

        sb.AppendLine("## Activity Type Mismatches");
        if (mismatches.Count == 0)
        {
            sb.AppendLine("- none");
        }
        else
        {
            foreach (var mismatch in mismatches.OrderBy(static x => x.EventName, StringComparer.Ordinal))
            {
                sb.AppendLine($"- {mismatch.EventName}");
                sb.AppendLine($"  - schema: {FormatTypes(mismatch.Schema)}");
                sb.AppendLine($"  - docs: {FormatTypes(mismatch.Docs)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Docs Only Events");
        if (docsOnly.Length == 0)
        {
            sb.AppendLine("- none");
        }
        else
        {
            foreach (var name in docsOnly)
            {
                sb.AppendLine($"- {name}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Schema Only Events");
        if (schemaOnly.Length == 0)
        {
            sb.AppendLine("- none");
        }
        else
        {
            foreach (var name in schemaOnly)
            {
                sb.AppendLine($"- {name}");
            }
        }

        File.WriteAllText(reportPath, sb.ToString().Replace("\r\n", "\n"));
    }

    static bool AreSameTypes(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        var leftNormalized = left
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();
        var rightNormalized = right
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        if (leftNormalized.Length != rightNormalized.Length)
        {
            return false;
        }

        for (var i = 0; i < leftNormalized.Length; i++)
        {
            if (!string.Equals(leftNormalized[i], rightNormalized[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    static string FormatTypes(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return "null";
        }

        if (values.Count == 0)
        {
            return "[]";
        }

        var normalized = values
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal);
        return "[" + string.Join(", ", normalized) + "]";
    }

    static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }
}
