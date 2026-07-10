using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

/// <summary>
/// Extracts <c>definitions.step</c> from GitHub workflow JSON Schema into a parsed-only model.
/// Does not apply supplemental overlays or compute per-form allowed keys.
/// </summary>
internal sealed class GitHubWorkflowStepSchemaParser
{
    private static readonly string[] SharedPropertyKeys =
    [
        "id",
        "if",
        "name",
        "env",
        "continue-on-error",
        "timeout-minutes",
    ];

    public StepSchemaParsedModel Parse(string schemaJson)
    {
        using var doc = JsonDocument.Parse(schemaJson);
        return Parse(doc.RootElement);
    }

    public StepSchemaParsedModel ParseFile(string schemaPath)
    {
        return Parse(File.ReadAllText(schemaPath));
    }

    internal StepSchemaParsedModel Parse(JsonElement root)
    {
        if (!root.TryGetProperty("definitions", out var definitions)
            || !definitions.TryGetProperty("step", out var step))
        {
            throw new InvalidDataException("github-workflow.schema.json is missing definitions.step.");
        }

        var forms = ParseForms(step);
        var properties = ParseProperties(step, definitions);
        var keyDependencies = ParseDependencies(step);

        return new StepSchemaParsedModel
        {
            SchemaVersion = 1,
            Source = "github-workflow-schema-raw",
            Forms = forms,
            Properties = properties,
            KeyDependencies = keyDependencies,
        };
    }

    private static IReadOnlyList<StepSchemaParsedFormModel> ParseForms(JsonElement step)
    {
        if (!step.TryGetProperty("oneOf", out var oneOf) || oneOf.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("definitions.step.oneOf must be an array.");
        }

        var forms = new List<StepSchemaParsedFormModel>();
        foreach (var branch in oneOf.EnumerateArray())
        {
            if (!branch.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var primaryKey = item.GetString();
                if (string.IsNullOrEmpty(primaryKey))
                {
                    continue;
                }

                forms.Add(new StepSchemaParsedFormModel
                {
                    Id = primaryKey,
                    PrimaryKey = primaryKey,
                });
            }
        }

        if (forms.Count == 0)
        {
            throw new InvalidDataException("definitions.step.oneOf produced no primary forms.");
        }

        forms.Sort(static (a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        return forms;
    }

    private static Dictionary<string, StepSchemaPropertyModel> ParseProperties(
        JsonElement step,
        JsonElement definitions)
    {
        if (!step.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, StepSchemaPropertyModel>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, StepSchemaPropertyModel>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            var valueKind = InferValueKind(property.Name, property.Value, definitions, out var expressionContext);
            result[property.Name] = new StepSchemaPropertyModel
            {
                ValueKind = valueKind,
                ExpressionContext = expressionContext,
            };
        }

        return result;
    }

    private static IReadOnlyList<StepSchemaKeyDependencyModel> ParseDependencies(JsonElement step)
    {
        if (!step.TryGetProperty("dependencies", out var dependencies)
            || dependencies.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<StepSchemaKeyDependencyModel>();
        foreach (var dependency in dependencies.EnumerateObject())
        {
            if (dependency.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var requiredPrimary in dependency.Value.EnumerateArray())
            {
                if (requiredPrimary.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var primary = requiredPrimary.GetString();
                if (string.IsNullOrEmpty(primary))
                {
                    continue;
                }

                result.Add(new StepSchemaKeyDependencyModel
                {
                    Key = dependency.Name,
                    RequiresPrimary = primary,
                });
            }
        }

        result.Sort(static (a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        return result;
    }

    internal static string InferValueKind(
        string propertyName,
        JsonElement schemaNode,
        JsonElement definitions,
        out string? expressionContext)
    {
        expressionContext = ResolveExpressionContext(propertyName);

        if (HasRef(schemaNode, "#/definitions/env"))
        {
            return "envMapping";
        }

        var resolved = ResolveSchemaNode(schemaNode, definitions);

        if (propertyName == "if")
        {
            return "stepIf";
        }

        if (ContainsExpressionSyntaxRef(resolved, definitions))
        {
            return propertyName switch
            {
                "continue-on-error" => "boolOrExpression",
                "timeout-minutes" => "floatOrExpression",
                _ => "string",
            };
        }

        if (resolved.TryGetProperty("type", out var typeElement))
        {
            return InferFromType(propertyName, typeElement, resolved, definitions);
        }

        if (resolved.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            return InferFromOneOf(propertyName, oneOf, definitions);
        }

        if (resolved.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
        {
            return InferFromAnyOf(anyOf, definitions);
        }

        throw new InvalidDataException($"Unable to infer valueKind for step property '{propertyName}'.");
    }

    private static string InferFromType(
        string propertyName,
        JsonElement typeElement,
        JsonElement resolved,
        JsonElement definitions)
    {
        if (typeElement.ValueKind == JsonValueKind.Array)
        {
            var types = typeElement.EnumerateArray()
                .Where(static t => t.ValueKind == JsonValueKind.String)
                .Select(static t => t.GetString())
                .Where(static t => !string.IsNullOrEmpty(t))
                .ToList();

            if (types.Contains("null", StringComparer.Ordinal)
                && types.Contains("boolean", StringComparer.Ordinal))
            {
                return "nullary";
            }
        }

        if (typeElement.ValueKind == JsonValueKind.String)
        {
            return typeElement.GetString() switch
            {
                "boolean" => "boolean",
                "string" => propertyName is "cancel" or "wait" ? "nonEmptyString" : "string",
                "number" => "floatOrExpression",
                "array" => InferArrayKind(resolved, definitions),
                _ => throw new InvalidDataException($"Unsupported type '{typeElement.GetString()}' for '{propertyName}'."),
            };
        }

        throw new InvalidDataException($"Unsupported type node for '{propertyName}'.");
    }

    private static string InferFromOneOf(string propertyName, JsonElement oneOf, JsonElement definitions)
    {
        var branches = oneOf.EnumerateArray().ToList();
        if (TryInferStringOrNonEmptyStringArray(branches))
        {
            return "stringOrNonEmptyStringArray";
        }

        if (branches.Any(static b => HasRef(b, "#/definitions/expressionSyntax")))
        {
            return propertyName switch
            {
                "continue-on-error" => "boolOrExpression",
                "timeout-minutes" => "floatOrExpression",
                _ => "string",
            };
        }

        throw new InvalidDataException($"Unable to infer oneOf valueKind for '{propertyName}'.");
    }

    private static bool TryInferStringOrNonEmptyStringArray(IReadOnlyList<JsonElement> branches)
    {
        if (!branches.Any(static b => b.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "string"))
        {
            return false;
        }

        foreach (var branch in branches)
        {
            if (!branch.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String
                || typeElement.GetString() != "array")
            {
                continue;
            }

            if (!branch.TryGetProperty("items", out var items)
                || !items.TryGetProperty("type", out var itemType)
                || itemType.ValueKind != JsonValueKind.String
                || itemType.GetString() != "string")
            {
                continue;
            }

            if (branch.TryGetProperty("minItems", out var minItems)
                && minItems.ValueKind == JsonValueKind.Number
                && minItems.GetInt32() >= 1)
            {
                return true;
            }
        }

        if (branches.Count == 2
            && branches.Any(static b => b.TryGetProperty("type", out var t)
                                        && t.ValueKind == JsonValueKind.Array
                                        && t.EnumerateArray().Any(static i => i.GetString() == "string")))
        {
            var arrayBranch = branches.First(static b =>
                b.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Array);
            return arrayBranch.TryGetProperty("minItems", out var minItems)
                   && minItems.ValueKind == JsonValueKind.Number
                   && minItems.GetInt32() >= 1;
        }

        return false;
    }

    private static string InferFromAnyOf(JsonElement anyOf, JsonElement definitions)
    {
        foreach (var branch in anyOf.EnumerateArray())
        {
            var resolved = ResolveSchemaNode(branch, definitions);
            if (resolved.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                && typeElement.GetString() == "string")
            {
                continue;
            }

            throw new InvalidDataException("Unsupported anyOf branch for step property.");
        }

        return "string";
    }

    private static string InferArrayKind(JsonElement resolved, JsonElement definitions)
    {
        if (!resolved.TryGetProperty("items", out var items))
        {
            throw new InvalidDataException("Array property is missing items.");
        }

        if (HasRef(items, "#/definitions/step"))
        {
            var minItems = resolved.TryGetProperty("minItems", out var min) && min.ValueKind == JsonValueKind.Number
                ? min.GetInt32()
                : 0;
            if (minItems < 1)
            {
                throw new InvalidDataException("parallel step array must have minItems >= 1.");
            }

            return "nonEmptyStepArray";
        }

        throw new InvalidDataException("Unsupported array items schema for step property.");
    }

    private static bool ContainsExpressionSyntaxRef(JsonElement node, JsonElement definitions)
    {
        if (HasRef(node, "#/definitions/expressionSyntax"))
        {
            return true;
        }

        if (node.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            foreach (var branch in oneOf.EnumerateArray())
            {
                if (ContainsExpressionSyntaxRef(ResolveSchemaNode(branch, definitions), definitions))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static JsonElement ResolveSchemaNode(JsonElement node, JsonElement definitions)
    {
        if (node.TryGetProperty("$ref", out var refElement) && refElement.ValueKind == JsonValueKind.String)
        {
            return ResolveRef(definitions, refElement.GetString()!);
        }

        return node;
    }

    private static JsonElement ResolveRef(JsonElement definitions, string refPath)
    {
        if (!refPath.StartsWith("#/definitions/", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported schema $ref: {refPath}");
        }

        var name = refPath["#/definitions/".Length..];
        if (!definitions.TryGetProperty(name, out var target))
        {
            throw new InvalidDataException($"Missing schema definition: {name}");
        }

        return target;
    }

    private static bool HasRef(JsonElement node, string refPath) =>
        node.TryGetProperty("$ref", out var refElement)
        && refElement.ValueKind == JsonValueKind.String
        && string.Equals(refElement.GetString(), refPath, StringComparison.Ordinal);

    private static string? ResolveExpressionContext(string propertyName) => propertyName switch
    {
        "run" => "StepRun",
        "shell" => "StepShell",
        "working-directory" => "StepWorkingDirectory",
        "continue-on-error" => "StepContinueOnError",
        "timeout-minutes" => "StepTimeoutMinutes",
        "if" => "StepIf",
        _ => null,
    };

    internal static bool IsSharedPropertyKey(string key) =>
        SharedPropertyKeys.Contains(key, StringComparer.Ordinal);
}
