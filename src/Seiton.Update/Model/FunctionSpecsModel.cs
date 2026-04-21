using System.Text.Json.Serialization;

namespace Seiton.Update.Model;

internal sealed record FunctionSpecsModel(IReadOnlyList<FunctionSpecEntry> Functions);

internal sealed record FunctionSpecEntry(
    string Name,
    IReadOnlyList<FunctionOverloadEntry> Overloads);

internal sealed record FunctionOverloadEntry(
    string ReturnType,
    IReadOnlyList<string> Params,
    [property: JsonPropertyName("variadicParam")] string? VariadicParam = null);
