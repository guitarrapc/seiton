#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0

using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

// Extract __NEXT_DATA__ JSON from webhook-events-and-payloads page
var url = "https://docs.github.com/en/webhooks/webhook-events-and-payloads";
using var client = new HttpClient();
client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
client.Timeout = TimeSpan.FromSeconds(30);

var html = await client.GetStringAsync(url);

// Extract __NEXT_DATA__ script content
var nextDataStart = html.IndexOf("<script id=\"__NEXT_DATA__\" type=\"application/json\">", StringComparison.Ordinal);
if (nextDataStart < 0) { Console.WriteLine("__NEXT_DATA__ not found"); return; }

var jsonStart = html.IndexOf('>', nextDataStart) + 1;
var jsonEnd = html.IndexOf("</script>", jsonStart, StringComparison.Ordinal);
var jsonText = html[jsonStart..jsonEnd];

using var doc = JsonDocument.Parse(jsonText);
var root = doc.RootElement;

// Navigate: props.pageProps.webhooks
var webhooks = root.GetProperty("props").GetProperty("pageProps").GetProperty("webhooks");
Console.WriteLine($"Total webhooks: {webhooks.GetArrayLength()}");

// Print first 3 webhook event structures in detail
var count = 0;
foreach (var webhook in webhooks.EnumerateArray())
{
    if (count >= 3) break;
    count++;

    var name = webhook.GetProperty("name").GetString();
    Console.WriteLine($"\n=== Event: {name} ===");

    // Check action types
    if (webhook.TryGetProperty("actionTypes", out var actionTypes))
    {
        Console.Write("  actionTypes: [");
        foreach (var at in actionTypes.EnumerateArray())
            Console.Write($"{at.GetString()}, ");
        Console.WriteLine("]");
    }

    // Check data structure
    if (webhook.TryGetProperty("data", out var data))
    {
        Console.WriteLine("  data keys: " + string.Join(", ", EnumKeys(data)));

        // Check bodyParameters
        if (data.TryGetProperty("bodyParameters", out var bodyParams))
        {
            Console.WriteLine($"  bodyParameters count: {bodyParams.GetArrayLength()}");
            foreach (var param in bodyParams.EnumerateArray())
            {
                var pName = param.GetProperty("name").GetString();
                var pType = param.GetProperty("type").GetString();
                var pIsRequired = param.TryGetProperty("isRequired", out var req) && req.GetBoolean();
                Console.Write($"    - {pName}: {pType}");
                if (pIsRequired) Console.Write(" (required)");

                // Check for childParamsGroups (nested properties)
                if (param.TryGetProperty("childParamsGroups", out var children) && children.GetArrayLength() > 0)
                    Console.Write($" [has {children.GetArrayLength()} child group(s)]");

                Console.WriteLine();
            }
        }
    }
}

// Print ALL event names
Console.WriteLine("\n=== All webhook event names ===");
foreach (var webhook in webhooks.EnumerateArray())
{
    Console.Write($"{webhook.GetProperty("name").GetString()}, ");
}
Console.WriteLine();

// Show structure of "push" event which has more complex types
Console.WriteLine("\n=== Push event details ===");
foreach (var webhook in webhooks.EnumerateArray())
{
    if (webhook.GetProperty("name").GetString() != "push") continue;

    var data = webhook.GetProperty("data");
    if (data.TryGetProperty("bodyParameters", out var bodyParams))
    {
        foreach (var param in bodyParams.EnumerateArray())
        {
            var pName = param.GetProperty("name").GetString();
            var pType = param.GetProperty("type").GetString();
            Console.Write($"  {pName}: {pType}");

            if (param.TryGetProperty("description", out var desc))
            {
                var descStr = desc.GetString();
                if (descStr?.Length > 80) descStr = descStr[..80] + "...";
                Console.Write($" -- {descStr}");
            }
            Console.WriteLine();
        }
    }
}

static IEnumerable<string> EnumKeys(JsonElement el)
{
    if (el.ValueKind == JsonValueKind.Object)
        foreach (var prop in el.EnumerateObject())
            yield return prop.Name;
}
