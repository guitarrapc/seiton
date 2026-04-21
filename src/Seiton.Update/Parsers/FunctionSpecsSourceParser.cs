using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class FunctionSpecsSourceParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public FunctionSpecsModel Parse(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        return JsonSerializer.Deserialize<FunctionSpecsModel>(json, Options)
            ?? throw new InvalidOperationException($"Failed to deserialize {jsonPath}");
    }
}
