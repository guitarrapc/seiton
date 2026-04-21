using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

internal sealed class ContextTypesSourceParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public ContextTypesModel Parse(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        return JsonSerializer.Deserialize<ContextTypesModel>(json, Options)
            ?? throw new InvalidOperationException($"Failed to deserialize {jsonPath}");
    }
}
