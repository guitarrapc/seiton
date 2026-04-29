using System.Text.Json;

var url = "https://json.schemastore.org/github-workflow.json";
using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Debug/1.0");
var content = await http.GetStringAsync(url);

using var doc = JsonDocument.Parse(content);
var root = doc.RootElement;

// Dump eventObject.oneOf
Console.WriteLine("=== eventObject ===");
var eventObjectDef = root.GetProperty("definitions").GetProperty("eventObject");
Console.WriteLine("Keys: " + string.Join(", ", eventObjectDef.EnumerateObject().Select(p => p.Name)));
if (eventObjectDef.TryGetProperty("oneOf", out var eoOneOf))
{
    for (int i = 0; i < eoOneOf.GetArrayLength(); i++)
    {
        var sub = eoOneOf[i];
        Console.WriteLine($"  oneOf[{i}] keys: {string.Join(", ", sub.EnumerateObject().Select(p => p.Name))}");
        if (sub.TryGetProperty("properties", out var sp))
        {
            Console.WriteLine($"    properties keys: {string.Join(", ", sp.EnumerateObject().Select(p => p.Name))}");
            if (sp.TryGetProperty("types", out var typesProp))
            {
                Console.WriteLine($"    types keys: {string.Join(", ", typesProp.EnumerateObject().Select(p => p.Name))}");
                if (typesProp.TryGetProperty("items", out var ti))
                    Console.WriteLine($"    types.items keys: {string.Join(", ", ti.EnumerateObject().Select(p => p.Name))}");
                if (typesProp.TryGetProperty("oneOf", out var too))
                    Console.WriteLine($"    types.oneOf count: {too.GetArrayLength()}");
            }
        }
    }
}

Console.WriteLine("\n=== types definition ===");
var typesDef = root.GetProperty("definitions").GetProperty("types");
Console.WriteLine("Keys: " + string.Join(", ", typesDef.EnumerateObject().Select(p => p.Name)));
if (typesDef.TryGetProperty("oneOf", out var typesOneOf))
{
    for (int i = 0; i < typesOneOf.GetArrayLength(); i++)
    {
        var sub = typesOneOf[i];
        Console.WriteLine($"  oneOf[{i}] keys: {string.Join(", ", sub.EnumerateObject().Select(p => p.Name))}");
    }
}

// Print all events in object form with brief details
Console.WriteLine("\n=== All events (object form) ===");
var onSchema = root.GetProperty("properties").GetProperty("on");
JsonElement objectForm = default;
foreach (var sub in onSchema.GetProperty("oneOf").EnumerateArray())
{
    if (sub.TryGetProperty("type", out var t) && t.GetString() == "object" && sub.TryGetProperty("properties", out _))
    {
        objectForm = sub;
        break;
    }
}

foreach (var ep in objectForm.GetProperty("properties").EnumerateObject())
{
    var name = ep.Name;
    var schema = ep.Value;

    bool hasRef = schema.TryGetProperty("$ref", out var refV);
    bool hasProps = schema.TryGetProperty("properties", out var propsV);
    bool hasOneOf = schema.TryGetProperty("oneOf", out _);
    bool hasTypes = hasProps && propsV.TryGetProperty("types", out _);

    string info = "";
    if (hasRef) info += $"$ref={refV.GetString()![^20..]} ";
    if (hasProps && hasTypes) info += "has_types_inline ";
    if (hasProps && !hasTypes) info += $"no_types(props:{string.Join(",", propsV.EnumerateObject().Select(p=>p.Name))}) ";
    if (!hasProps && !hasOneOf && !hasRef) info += "empty";
    if (hasOneOf) info += "has_oneOf ";

    Console.WriteLine($"  {name}: {info}");
}
