using Seiton.Update.Model;
using Seiton.Update.Parsers;

namespace Seiton.Update.Services;

/// <summary>
/// Merges parsed schema extraction with supplemental overlays into the canonical snapshot.
/// </summary>
internal sealed class StepSchemaMerger
{
    public StepSchemaModel Merge(
        StepSchemaParsedModel parsed,
        StepSchemaSupplementalModel supplemental,
        IReadOnlyList<RawSourceRef>? canonicalRawSources = null)
    {
        var properties = new Dictionary<string, StepSchemaPropertyModel>(StringComparer.Ordinal);
        foreach (var pair in parsed.Properties)
        {
            properties[pair.Key] = pair.Value;
        }

        foreach (var pair in supplemental.AdditionalProperties)
        {
            properties[pair.Key] = pair.Value;
        }

        var formsById = new Dictionary<string, MergedFormBuilder>(StringComparer.Ordinal);
        foreach (var form in parsed.Forms)
        {
            formsById[form.Id] = new MergedFormBuilder(form.Id, form.PrimaryKey);
        }

        foreach (var additional in supplemental.AdditionalForms)
        {
            if (!formsById.ContainsKey(additional.Id))
            {
                formsById[additional.Id] = new MergedFormBuilder(additional.Id, additional.PrimaryKey);
            }

            var builder = formsById[additional.Id];
            if (!string.IsNullOrEmpty(additional.UnexpectedKeyDescription))
            {
                builder.UnexpectedKeyDescription = additional.UnexpectedKeyDescription;
            }

            foreach (var key in additional.DisallowedKeys)
            {
                builder.DisallowedKeys.Add(key);
            }

            foreach (var pair in additional.Properties)
            {
                builder.ExplicitProperties[pair.Key] = pair.Value;
                properties.TryAdd(pair.Key, pair.Value);
            }
        }

        var overlayById = supplemental.FormOverlays.ToDictionary(static o => o.Id, StringComparer.Ordinal);
        foreach (var (id, builder) in formsById)
        {
            if (overlayById.TryGetValue(id, out var overlay))
            {
                if (!string.IsNullOrEmpty(overlay.UnexpectedKeyDescription))
                {
                    builder.UnexpectedKeyDescription = overlay.UnexpectedKeyDescription;
                }

                foreach (var key in overlay.DisallowedKeys)
                {
                    builder.DisallowedKeys.Add(key);
                }
            }
        }

        ApplyDefaultDescriptions(formsById);

        var allPrimaryKeys = formsById.Values.Select(static f => f.PrimaryKey).ToHashSet(StringComparer.Ordinal);
        var sharedKeys = supplemental.SharedKeys.Count > 0
            ? supplemental.SharedKeys.OrderBy(static k => k, StringComparer.Ordinal).ToList()
            : parsed.Properties.Keys.Where(GitHubWorkflowStepSchemaParser.IsSharedPropertyKey).OrderBy(static k => k, StringComparer.Ordinal).ToList();

        var modifierByKey = supplemental.Modifiers.ToDictionary(static m => m.Key, StringComparer.Ordinal);
        var dependencyByKey = parsed.KeyDependencies.ToDictionary(static d => d.Key, StringComparer.Ordinal);

        var forms = new List<StepSchemaFormModel>();
        foreach (var builder in formsById.Values.OrderBy(static f => f.Id, StringComparer.Ordinal))
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in sharedKeys)
            {
                allowed.Add(key);
            }

            allowed.Add(builder.PrimaryKey);

            foreach (var modifier in modifierByKey.Values)
            {
                if (modifier.AllowedOnFormIds.Contains(builder.Id, StringComparer.Ordinal))
                {
                    allowed.Add(modifier.Key);
                }
            }

            foreach (var pair in properties)
            {
                if (allPrimaryKeys.Contains(pair.Key) && !string.Equals(pair.Key, builder.PrimaryKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (builder.DisallowedKeys.Contains(pair.Key))
                {
                    continue;
                }

                if (dependencyByKey.TryGetValue(pair.Key, out var dependency)
                    && !string.Equals(dependency.RequiresPrimary, builder.PrimaryKey, StringComparison.Ordinal))
                {
                    continue;
                }

                allowed.Add(pair.Key);
            }

            foreach (var pair in builder.ExplicitProperties)
            {
                allowed.Add(pair.Key);
            }

            var formProperties = new Dictionary<string, StepSchemaPropertyModel>(StringComparer.Ordinal);
            foreach (var key in allowed)
            {
                if (properties.TryGetValue(key, out var property))
                {
                    formProperties[key] = property;
                }
                else if (builder.ExplicitProperties.TryGetValue(key, out var explicitProperty))
                {
                    formProperties[key] = explicitProperty;
                }
            }

            forms.Add(new StepSchemaFormModel
            {
                Id = builder.Id,
                PrimaryKey = builder.PrimaryKey,
                UnexpectedKeyDescription = builder.UnexpectedKeyDescription
                    ?? throw new InvalidOperationException($"Missing unexpectedKeyDescription for form '{builder.Id}'."),
                AllowedKeys = allowed.OrderBy(static k => k, StringComparer.Ordinal).ToList(),
                Properties = formProperties,
            });
        }

        return new StepSchemaModel
        {
            SchemaVersion = 1,
            Source = "github-workflow-schema+supplemental",
            RawSources = canonicalRawSources ?? parsed.RawSources,
            AppliesTo = supplemental.AppliesTo.Count > 0
                ? supplemental.AppliesTo
                : ["workflow-job-steps", "action-metadata-steps"],
            SharedKeys = sharedKeys,
            Forms = forms,
            Modifiers = supplemental.Modifiers,
            KeyDependencies = parsed.KeyDependencies,
        };
    }

    private static void ApplyDefaultDescriptions(Dictionary<string, MergedFormBuilder> formsById)
    {
        foreach (var builder in formsById.Values)
        {
            if (!string.IsNullOrEmpty(builder.UnexpectedKeyDescription))
            {
                continue;
            }

            builder.UnexpectedKeyDescription = builder.Id switch
            {
                "run" => "step to run shell command",
                "uses" => "step to execute action",
                "wait" => "step to wait for background steps",
                "wait-all" => "step to wait for all background steps",
                "cancel" => "step to cancel a background step",
                "parallel" => "step to run steps in parallel",
                _ => $"step to use '{builder.PrimaryKey}'",
            };
        }
    }

    private sealed class MergedFormBuilder(string id, string primaryKey)
    {
        public string Id { get; } = id;
        public string PrimaryKey { get; } = primaryKey;
        public string? UnexpectedKeyDescription { get; set; }
        public HashSet<string> DisallowedKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, StepSchemaPropertyModel> ExplicitProperties { get; } = new(StringComparer.Ordinal);
    }
}
