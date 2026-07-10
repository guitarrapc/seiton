using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class StepSchemaSourceParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public StepSchemaModel Parse(string path)
    {
        var json = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<StepSchemaSnapshot>(json, JsonOptions)
            ?? throw new InvalidDataException($"Invalid step-schema snapshot: {path}");

        if (snapshot.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported step-schema schemaVersion: {snapshot.SchemaVersion}");
        }

        return new StepSchemaModel
        {
            SchemaVersion = snapshot.SchemaVersion,
            Source = snapshot.Source ?? string.Empty,
            RawSources = snapshot.RawSources ?? [],
            AppliesTo = snapshot.AppliesTo ?? [],
            SharedKeys = snapshot.SharedKeys ?? [],
            Forms = snapshot.Forms?.Select(ToFormModel).ToList() ?? [],
            Modifiers = snapshot.Modifiers?.Select(ToModifierModel).ToList() ?? [],
            KeyDependencies = snapshot.KeyDependencies?.Select(ToDependencyModel).ToList() ?? [],
        };
    }

    public StepSchemaSupplementalModel ParseSupplemental(string path)
    {
        var json = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<StepSchemaSupplementalSnapshot>(json, JsonOptions)
            ?? throw new InvalidDataException($"Invalid supplemental step-schema: {path}");

        return new StepSchemaSupplementalModel
        {
            SchemaVersion = snapshot.SchemaVersion,
            Description = snapshot.Description,
            AppliesTo = snapshot.AppliesTo ?? [],
            SharedKeys = snapshot.SharedKeys ?? [],
            Modifiers = snapshot.Modifiers?.Select(ToModifierModel).ToList() ?? [],
            FormOverlays = snapshot.FormOverlays?.Select(ToOverlayModel).ToList() ?? [],
            AdditionalForms = snapshot.AdditionalForms?.Select(ToAdditionalFormModel).ToList() ?? [],
            AdditionalProperties = snapshot.AdditionalProperties?.ToDictionary(
                static p => p.Key,
                static p => ToPropertyModel(p.Value),
                StringComparer.Ordinal) ?? new Dictionary<string, StepSchemaPropertyModel>(StringComparer.Ordinal),
        };
    }

    private static StepSchemaFormModel ToFormModel(StepSchemaFormSnapshot snapshot) => new()
    {
        Id = snapshot.Id ?? string.Empty,
        PrimaryKey = snapshot.PrimaryKey ?? string.Empty,
        UnexpectedKeyDescription = snapshot.UnexpectedKeyDescription ?? string.Empty,
        AllowedKeys = snapshot.AllowedKeys ?? [],
        Properties = snapshot.Properties?.ToDictionary(
            static p => p.Key,
            static p => ToPropertyModel(p.Value),
            StringComparer.Ordinal) ?? new Dictionary<string, StepSchemaPropertyModel>(StringComparer.Ordinal),
    };

    private static StepSchemaModifierModel ToModifierModel(StepSchemaModifierSnapshot snapshot) => new()
    {
        Key = snapshot.Key ?? string.Empty,
        AllowedOnFormIds = snapshot.AllowedOnFormIds ?? [],
    };

    private static StepSchemaKeyDependencyModel ToDependencyModel(StepSchemaKeyDependencySnapshot snapshot) => new()
    {
        Key = snapshot.Key ?? string.Empty,
        RequiresPrimary = snapshot.RequiresPrimary ?? string.Empty,
    };

    private static StepSchemaSupplementalFormOverlayModel ToOverlayModel(StepSchemaFormOverlaySnapshot snapshot) => new()
    {
        Id = snapshot.Id ?? string.Empty,
        UnexpectedKeyDescription = snapshot.UnexpectedKeyDescription,
        DisallowedKeys = snapshot.DisallowedKeys ?? [],
    };

    private static StepSchemaSupplementalAdditionalFormModel ToAdditionalFormModel(
        StepSchemaAdditionalFormSnapshot snapshot) => new()
        {
            Id = snapshot.Id ?? string.Empty,
            PrimaryKey = snapshot.PrimaryKey ?? string.Empty,
            UnexpectedKeyDescription = snapshot.UnexpectedKeyDescription,
            DisallowedKeys = snapshot.DisallowedKeys ?? [],
            Properties = snapshot.Properties?.ToDictionary(
            static p => p.Key,
            static p => ToPropertyModel(p.Value),
            StringComparer.Ordinal) ?? new Dictionary<string, StepSchemaPropertyModel>(StringComparer.Ordinal),
        };

    private static StepSchemaPropertyModel ToPropertyModel(StepSchemaPropertySnapshot snapshot) => new()
    {
        ValueKind = snapshot.ValueKind ?? string.Empty,
        ExpressionContext = snapshot.ExpressionContext,
    };

    private sealed class StepSchemaSnapshot
    {
        public int SchemaVersion { get; set; }
        public string? Source { get; set; }
        public List<RawSourceRef>? RawSources { get; set; }
        public List<string>? AppliesTo { get; set; }
        public List<string>? SharedKeys { get; set; }
        public List<StepSchemaFormSnapshot>? Forms { get; set; }
        public List<StepSchemaModifierSnapshot>? Modifiers { get; set; }
        public List<StepSchemaKeyDependencySnapshot>? KeyDependencies { get; set; }
    }

    private sealed class StepSchemaSupplementalSnapshot
    {
        public int SchemaVersion { get; set; }
        public string? Description { get; set; }
        public List<string>? AppliesTo { get; set; }
        public List<string>? SharedKeys { get; set; }
        public List<StepSchemaModifierSnapshot>? Modifiers { get; set; }
        public List<StepSchemaFormOverlaySnapshot>? FormOverlays { get; set; }
        public List<StepSchemaAdditionalFormSnapshot>? AdditionalForms { get; set; }
        public Dictionary<string, StepSchemaPropertySnapshot>? AdditionalProperties { get; set; }
    }

    private sealed class StepSchemaFormSnapshot
    {
        public string? Id { get; set; }
        public string? PrimaryKey { get; set; }
        public string? UnexpectedKeyDescription { get; set; }
        public List<string>? AllowedKeys { get; set; }
        public Dictionary<string, StepSchemaPropertySnapshot>? Properties { get; set; }
    }

    private sealed class StepSchemaFormOverlaySnapshot
    {
        public string? Id { get; set; }
        public string? UnexpectedKeyDescription { get; set; }
        public List<string>? DisallowedKeys { get; set; }
    }

    private sealed class StepSchemaAdditionalFormSnapshot
    {
        public string? Id { get; set; }
        public string? PrimaryKey { get; set; }
        public string? UnexpectedKeyDescription { get; set; }
        public List<string>? DisallowedKeys { get; set; }
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
