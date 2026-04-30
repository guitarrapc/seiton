using System.Text.Json.Serialization;

namespace Seiton.Playground;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<PlaygroundDiagnosticDto>))]
internal partial class PlaygroundJsonSerializerContext : JsonSerializerContext;
