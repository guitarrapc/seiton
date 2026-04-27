using System.Text.Json;
using Seiton.Update.Model;

namespace Seiton.Update.Parsers;

/// <summary>
/// Parses the rendered GitHub Docs webhook-events-and-payloads page HTML.
///
/// The page embeds structured JSON in a <c>&lt;script id="__NEXT_DATA__"&gt;</c> tag.
/// The relevant structure is:
/// <c>props.pageProps.webhooks[].{name, data.bodyParameters[].{name, type}}</c>.
///
/// Each <c>bodyParameters</c> entry has:
/// - <c>name</c>: property name (e.g., "action", "sender")
/// - <c>type</c>: type string (e.g., "string", "object", "array of objects", "string or null")
/// - <c>isRequired</c>: boolean indicating if the property is required
///
/// For events with multiple action types, <c>bodyParameters</c> in <c>data</c> represents
/// the first action type. Other action types are in <c>data.actionTypes[].bodyParameters</c>,
/// but the top-level properties are consistent across action types for our purposes.
/// </summary>
internal sealed class WebhookEventPayloadDocsParser
{
    /// <summary>
    /// Workflow trigger events that are NOT on the webhook events page and need supplemental entries.
    /// These are GitHub Actions-specific events that produce synthetic payloads.
    /// </summary>
    private static readonly EventPayloadEntry[] SupplementalEvents =
    [
        // schedule events have a synthetic payload with just the cron expression
        new("schedule", [new("schedule", "string")]),

        // pull_request_target uses the same payload structure as pull_request webhook
        // but is not listed separately on the webhook events page
    ];

    /// <summary>
    /// Parses the raw HTML from the webhook-events-and-payloads page and extracts
    /// event payload type information from the embedded __NEXT_DATA__ JSON.
    /// </summary>
    public EventPayloadTypesModel Parse(string htmlContent)
    {
        var nextDataJson = ExtractNextDataJson(htmlContent);
        using var doc = JsonDocument.Parse(nextDataJson);
        var root = doc.RootElement;

        var webhooks = root.GetProperty("props").GetProperty("pageProps").GetProperty("webhooks");
        var events = new List<EventPayloadEntry>();

        foreach (var webhook in webhooks.EnumerateArray())
        {
            var name = webhook.GetProperty("name").GetString()
                ?? throw new InvalidDataException("Webhook entry missing 'name'.");

            if (!webhook.TryGetProperty("data", out var data))
                continue;

            if (!data.TryGetProperty("bodyParameters", out var bodyParams))
                continue;

            var properties = ParseBodyParameters(bodyParams);
            events.Add(new EventPayloadEntry(name, properties));
        }

        // Add pull_request_target by cloning pull_request payload (if present)
        var pullRequestEntry = events.FirstOrDefault(e => e.Name == "pull_request");
        if (pullRequestEntry is not null)
        {
            var prTargetExists = events.Any(e => e.Name == "pull_request_target");
            if (!prTargetExists)
            {
                events.Add(new EventPayloadEntry("pull_request_target", pullRequestEntry.Properties));
            }
        }

        // Add supplemental events
        foreach (var supplemental in SupplementalEvents)
        {
            if (!events.Any(e => e.Name == supplemental.Name))
            {
                events.Add(supplemental);
            }
        }

        // Sort by name for deterministic output
        events.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        return new EventPayloadTypesModel(2, "github-docs-webhook-events-and-payloads", events);
    }

    /// <summary>
    /// Extracts the __NEXT_DATA__ JSON string from the HTML page.
    /// </summary>
    internal static string ExtractNextDataJson(string htmlContent)
    {
        const string marker = "<script id=\"__NEXT_DATA__\" type=\"application/json\">";
        var start = htmlContent.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidDataException("__NEXT_DATA__ script tag not found in HTML content.");

        var jsonStart = start + marker.Length;
        var jsonEnd = htmlContent.IndexOf("</script>", jsonStart, StringComparison.Ordinal);
        if (jsonEnd < 0)
            throw new InvalidDataException("Closing </script> tag not found after __NEXT_DATA__.");

        return htmlContent[jsonStart..jsonEnd];
    }

    /// <summary>
    /// Parses bodyParameters array into property entries.
    /// Only extracts top-level properties (skips nested childParamsGroups).
    /// </summary>
    internal static IReadOnlyList<EventPayloadPropertyEntry> ParseBodyParameters(JsonElement bodyParams)
    {
        var properties = new List<EventPayloadPropertyEntry>();

        foreach (var param in bodyParams.EnumerateArray())
        {
            var name = param.GetProperty("name").GetString()
                ?? throw new InvalidDataException("Body parameter missing 'name'.");

            var typeStr = param.GetProperty("type").GetString()
                ?? throw new InvalidDataException($"Body parameter '{name}' missing 'type'.");

            var (type, elementType) = MapType(typeStr);

            properties.Add(elementType is not null
                ? new EventPayloadPropertyEntry(name, type, new EventPayloadElementTypeEntry(elementType))
                : new EventPayloadPropertyEntry(name, type));
        }

        // Sort by name for deterministic output
        properties.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        return properties;
    }

    /// <summary>
    /// Maps a docs type string to our simple type system.
    /// Returns (type, elementType) where elementType is non-null for arrays.
    /// </summary>
    internal static (string Type, string? ElementType) MapType(string docsType)
    {
        // Normalize: strip " or null" suffix, trim whitespace
        var normalized = docsType.Trim();
        if (normalized.EndsWith(" or null", StringComparison.Ordinal))
        {
            normalized = normalized[..^" or null".Length].Trim();
        }

        return normalized switch
        {
            "string" => ("string", null),
            "object" => ("object", null),
            "number" => ("number", null),
            "integer" => ("number", null),
            "boolean" => ("bool", null),
            "array of objects" => ("array", "object"),
            "array of strings" => ("array", "string"),
            _ => ("any", null),
        };
    }
}
