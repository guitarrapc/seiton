namespace Seiton.Update.Model;

/// <summary>Canonical merged snapshot consumed by <c>sync-step-schema</c>.</summary>
internal sealed class StepSchemaModel
{
    public required int SchemaVersion { get; init; }
    public required string Source { get; init; }
    public IReadOnlyList<RawSourceRef> RawSources { get; init; } = [];
    public IReadOnlyList<string> AppliesTo { get; init; } = [];
    public IReadOnlyList<string> SharedKeys { get; init; } = [];
    public IReadOnlyList<StepSchemaFormModel> Forms { get; init; } = [];
    public IReadOnlyList<StepSchemaModifierModel> Modifiers { get; init; } = [];
    public IReadOnlyList<StepSchemaKeyDependencyModel> KeyDependencies { get; init; } = [];
}

/// <summary>Stage-2 artifact: schema extraction only (no supplemental overlays).</summary>
internal sealed class StepSchemaParsedModel
{
    public required int SchemaVersion { get; init; }
    public required string Source { get; init; }
    public IReadOnlyList<RawSourceRef> RawSources { get; init; } = [];
    public IReadOnlyList<StepSchemaParsedFormModel> Forms { get; init; } = [];
    public IReadOnlyDictionary<string, StepSchemaPropertyModel> Properties { get; init; }
        = new Dictionary<string, StepSchemaPropertyModel>(StringComparer.Ordinal);
    public IReadOnlyList<StepSchemaKeyDependencyModel> KeyDependencies { get; init; } = [];
}

internal sealed class StepSchemaParsedFormModel
{
    public required string Id { get; init; }
    public required string PrimaryKey { get; init; }
}

internal sealed class StepSchemaFormModel
{
    public required string Id { get; init; }
    public required string PrimaryKey { get; init; }
    public required string UnexpectedKeyDescription { get; init; }
    public IReadOnlyList<string> AllowedKeys { get; init; } = [];
    public IReadOnlyDictionary<string, StepSchemaPropertyModel> Properties { get; init; }
        = new Dictionary<string, StepSchemaPropertyModel>(StringComparer.Ordinal);
}

internal sealed class StepSchemaPropertyModel
{
    public required string ValueKind { get; init; }
    public string? ExpressionContext { get; init; }
}

internal sealed class StepSchemaModifierModel
{
    public required string Key { get; init; }
    public IReadOnlyList<string> AllowedOnFormIds { get; init; } = [];
}

internal sealed class StepSchemaKeyDependencyModel
{
    public required string Key { get; init; }
    public required string RequiresPrimary { get; init; }
}

/// <summary>Hand-written supplemental overlay (never mixed into parsed/).</summary>
internal sealed class StepSchemaSupplementalModel
{
    public int SchemaVersion { get; init; } = 1;
    public string? Description { get; init; }
    public IReadOnlyList<string> AppliesTo { get; init; } = [];
    public IReadOnlyList<string> SharedKeys { get; init; } = [];
    public IReadOnlyList<StepSchemaModifierModel> Modifiers { get; init; } = [];
    public IReadOnlyList<StepSchemaSupplementalFormOverlayModel> FormOverlays { get; init; } = [];
    public IReadOnlyList<StepSchemaSupplementalAdditionalFormModel> AdditionalForms { get; init; } = [];
    public IReadOnlyDictionary<string, StepSchemaPropertyModel> AdditionalProperties { get; init; }
        = new Dictionary<string, StepSchemaPropertyModel>(StringComparer.Ordinal);
}

internal sealed class StepSchemaSupplementalFormOverlayModel
{
    public required string Id { get; init; }
    public string? UnexpectedKeyDescription { get; init; }
    public IReadOnlyList<string> DisallowedKeys { get; init; } = [];
}

internal sealed class StepSchemaSupplementalAdditionalFormModel
{
    public required string Id { get; init; }
    public required string PrimaryKey { get; init; }
    public string? UnexpectedKeyDescription { get; init; }
    public IReadOnlyDictionary<string, StepSchemaPropertyModel> Properties { get; init; }
        = new Dictionary<string, StepSchemaPropertyModel>(StringComparer.Ordinal);
    public IReadOnlyList<string> DisallowedKeys { get; init; } = [];
}
