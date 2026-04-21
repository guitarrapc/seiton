using System.Text.Json.Serialization;

namespace Seiton.Update.Model;

internal sealed record ContextTypesModel(IReadOnlyList<ContextEntry> Contexts);

internal sealed record ContextEntry(
    string Name,
    bool? Strict = null,
    [property: JsonPropertyName("dynamicPropertyType")] string? DynamicPropertyType = null,
    IReadOnlyList<ContextPropertyEntry>? Properties = null);

internal sealed record ContextPropertyEntry(
    string Name,
    string Type,
    bool? Strict = null,
    bool? Undocumented = null,
    [property: JsonPropertyName("dynamicPropertyType")] string? DynamicPropertyType = null,
    IReadOnlyList<ContextPropertyEntry>? Properties = null,
    [property: JsonPropertyName("dynamicPropertyObject")] ContextPropertyObjectEntry? DynamicPropertyObject = null);

internal sealed record ContextPropertyObjectEntry(
    bool? Strict = null,
    IReadOnlyList<ContextPropertyEntry>? Properties = null);
