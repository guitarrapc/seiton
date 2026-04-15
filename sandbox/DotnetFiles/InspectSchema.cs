using System.Text.Json;
using System.Text.Json.Nodes;

// Fetch and inspect the SchemaStore github-workflow.json structure for key events
var url = "https://json.schemastore.org/github-workflow.json";
using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Debug/1.0");
var content = await http.GetStringAsync(url);

using var doc = JsonDocument.Parse(content);
var root = doc.RootElement;

// Find on.oneOf
var onSchema = root.GetProperty("properties").GetProperty("on");
Console.WriteLine("on.keys: " + string.Join(", ", onSchema.EnumerateObject().Select(p => p.Name)));
Console.WriteLine();

// Find if there's a oneOf
if (onSchema.TryGetProperty("oneOf", out var oneOf))
{
    Console.WriteLine($"on.oneOf has {oneOf.GetArrayLength()} entries");
    for (int i = 0; i < oneOf.GetArrayLength(); i++)
    {
        var sub = oneOf[i];
        var keys = sub.EnumerateObject().Select(p => p.Name).ToList();
        Console.WriteLine($"  [{i}] keys: {string.Join(", ", keys)}");
        if (sub.TryGetProperty("type", out var t))
        {
            Console.WriteLine($"       type={t}");
        }
    }
    Console.WriteLine();
}

// Find the object form
JsonElement objectForm = default;
if (onSchema.TryGetProperty("oneOf", out var oneOf2))
{
    foreach (var sub in oneOf2.EnumerateArray())
    {
        if (sub.TryGetProperty("type", out var t) && t.GetString() == "object"
            && sub.TryGetProperty("properties", out _))
        {
            objectForm = sub;
            break;
        }
    }
}

if (objectForm.ValueKind == JsonValueKind.Undefined)
{
    Console.WriteLine("No object form found in oneOf. Let's dump the full structure of 'on':");
    // Try to find anyOf or other combiners
    foreach (var prop in onSchema.EnumerateObject())
    {
        Console.WriteLine($"  on.{prop.Name}: {prop.Value.ValueKind}");
        if (prop.Value.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < prop.Value.GetArrayLength(); i++)
            {
                var sub = prop.Value[i];
                Console.Write($"    [{i}]: ");
                if (sub.TryGetProperty("type", out var t))
                    Console.Write($"type={t} ");
                if (sub.TryGetProperty("$ref", out var r))
                    Console.Write($"$ref={r} ");
                Console.WriteLine(string.Join(", ", sub.EnumerateObject().Select(p => p.Name)));
            }
        }
    }

    // Maybe the structure is in a definitions section
    Console.WriteLine();
    Console.WriteLine("Looking for event list in definitions...");
    if (root.TryGetProperty("definitions", out var defs))
    {
        Console.WriteLine("definitions keys: " + string.Join(", ", defs.EnumerateObject().Select(p => p.Name).Take(20)));
    }
}
else
{
    Console.WriteLine("Found object form. Event properties:");
    Console.WriteLine("\n=== eventObject definition ===");
    if (root.TryGetProperty("definitions", out var defsEl))
    {
        if (defsEl.TryGetProperty("eventObject", out var eoEl))
        {
            Console.WriteLine("eventObject keys: " + string.Join(", ", eoEl.EnumerateObject().Select(p => p.Name)));
            if (eoEl.TryGetProperty("properties", out var eoProps))
                Console.WriteLine("eventObject.properties: " + string.Join(", ", eoProps.EnumerateObject().Select(p => p.Name)));
            if (eoEl.TryGetProperty("anyOf", out var eoAnyOf))
            {
                for (int i = 0; i < eoAnyOf.GetArrayLength(); i++)
                {
                    var sub = eoAnyOf[i];
                    Console.WriteLine($"  anyOf[{i}] keys: {string.Join(",", sub.EnumerateObject().Select(p=>p.Name))}");
                    if (sub.TryGetProperty("properties", out var sp))
                        Console.WriteLine($"    properties: {string.Join(",", sp.EnumerateObject().Select(p=>p.Name))}");
                }
            }
        }

        if (defsEl.TryGetProperty("types", out var typesEl))
        {
            Console.WriteLine("\ntypes definition keys: " + string.Join(", ", typesEl.EnumerateObject().Select(p => p.Name)));
            if (typesEl.TryGetProperty("items", out var ti))
                Console.WriteLine("types.items keys: " + string.Join(", ", ti.EnumerateObject().Select(p => p.Name)));
        }
    }
    Console.WriteLine();
    Console.WriteLine("Found object form. Event properties:");
    var props = objectForm.GetProperty("properties");
    foreach (var ep in props.EnumerateObject())
    {
        var eventName = ep.Name;
        var eventSchema = ep.Value;

        // Show the raw structure for well-known events
        if (eventName is "issues" or "pull_request" or "pull_request_target"
            or "repository_dispatch" or "workflow_run" or "check_run")
                if (eventName is "issues" or "pull_request" or "pull_request_target"
                    or "repository_dispatch" or "workflow_run" or "check_run" or "watch")
        {
            Console.WriteLine($"\n=== {eventName} ===");
            Console.WriteLine("keys: " + string.Join(", ", eventSchema.EnumerateObject().Select(p => p.Name)));

            if (eventSchema.TryGetProperty("$ref", out var refVal))
            {
                Console.WriteLine($"  -> $ref: {refVal}");
            }

            if (eventSchema.TryGetProperty("properties", out var eProps))
            {
                Console.WriteLine("  properties: " + string.Join(", ", eProps.EnumerateObject().Select(p => p.Name)));
                if (eProps.TryGetProperty("types", out var typesProp))
                {
                    Console.WriteLine("  types keys: " + string.Join(", ", typesProp.EnumerateObject().Select(p => p.Name)));
                    if (typesProp.TryGetProperty("items", out var items))
                    {
                        Console.WriteLine("  types.items keys: " + string.Join(", ", items.EnumerateObject().Select(p => p.Name)));
                        if (items.TryGetProperty("enum", out var enumEl))
                        {
                            Console.WriteLine("  types.items.enum: " + string.Join(", ", enumEl.EnumerateArray().Select(x => x.GetString())));
                        }
                    }
                }
            }

            if (eventSchema.TryGetProperty("oneOf", out var eo))
            {
                Console.WriteLine($"  oneOf has {eo.GetArrayLength()} entries");
                for (int i = 0; i < eo.GetArrayLength(); i++)
                {
                    var subi = eo[i];
                    Console.WriteLine($"    [{i}] valudKind={subi.ValueKind}, keys={string.Join(",", subi.EnumerateObject().Select(p=>p.Name))}");
                }
            }

            if (eventSchema.TryGetProperty("anyOf", out var eao))
            {
                Console.WriteLine($"  anyOf has {eao.GetArrayLength()} entries");
                for (int i = 0; i < eao.GetArrayLength(); i++)
                {
                    var subi = eao[i];
                    Console.WriteLine($"    [{i}] valueKind={subi.ValueKind}, keys={string.Join(",", subi.EnumerateObject().Select(p=>p.Name))}");
                }
            }

            if (eventSchema.TryGetProperty("allOf", out var eall))
            {
                Console.WriteLine($"  allOf has {eall.GetArrayLength()} entries");
                for (int i = 0; i < eall.GetArrayLength(); i++)
                {
                    var subi = eall[i];
                    Console.WriteLine($"    [{i}] valueKind={subi.ValueKind}, keys={string.Join(",", subi.EnumerateObject().Select(p=>p.Name))}");
                }
            }
        }
    }
}
