using System.Text.Json.Serialization;

namespace Seiton.Update.Model;

/// <summary>
/// Root model for context-types-override.json — hand-written overrides that are merged
/// with the parsed docs snapshot (docs-contexts.json) to produce the canonical context-types.json.
/// </summary>
internal sealed record ContextTypesOverrideModel(
    [property: JsonPropertyName("contextOverrides")]
    IReadOnlyList<ContextOverrideEntry> ContextOverrides);

/// <summary>
/// Override data for a single context.
/// - <see cref="Strict"/>: whether the context object is strict (not from docs).
/// - <see cref="DynamicPropertyType"/>: root-level dynamic property type (not reliably from docs).
/// - <see cref="PropertyOverrides"/>: replaces individual documented properties with detailed entries
///   (e.g. nested object schemas, type corrections, or additional metadata).
/// - <see cref="UndocumentedProperties"/>: extra properties not in official docs (written to context-types.json
///   with <c>undocumented: true</c>).
/// </summary>
internal sealed record ContextOverrideEntry(
    string Name,
    bool? Strict = null,
    [property: JsonPropertyName("dynamicPropertyType")] string? DynamicPropertyType = null,
    [property: JsonPropertyName("propertyOverrides")] IReadOnlyList<ContextPropertyEntry>? PropertyOverrides = null,
    [property: JsonPropertyName("undocumentedProperties")] IReadOnlyList<ContextPropertyEntry>? UndocumentedProperties = null);
