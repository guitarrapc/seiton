using System.Text.Json.Serialization;

namespace Seiton.Update.Model;

internal sealed record EventPayloadTypesModel(
    int SchemaVersion,
    string Source,
    IReadOnlyList<EventPayloadEntry> Events);

internal sealed record EventPayloadEntry(
    string Name,
    IReadOnlyList<EventPayloadPropertyEntry> Properties);

internal sealed record EventPayloadPropertyEntry(
    string Name,
    string Type,
    [property: JsonPropertyName("elementType")] EventPayloadElementTypeEntry? ElementType = null);

internal sealed record EventPayloadElementTypeEntry(
    string Type);
