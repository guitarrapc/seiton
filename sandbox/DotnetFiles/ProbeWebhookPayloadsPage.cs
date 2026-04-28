#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0

using System.Net.Http;
using System.Text.RegularExpressions;

// Probe the GitHub Docs webhook-events-and-payloads page to understand HTML structure
var url = "https://docs.github.com/en/webhooks/webhook-events-and-payloads";
using var client = new HttpClient();
client.DefaultRequestHeaders.UserAgent.ParseAdd("Seiton.Update/1.0");
client.Timeout = TimeSpan.FromSeconds(30);

Console.WriteLine($"Fetching {url}...");
var html = await client.GetStringAsync(url);
Console.WriteLine($"Downloaded {html.Length} bytes");

// Check if it's an SPA (minimal content) or server-rendered (content present)
var h2Pattern = new Regex(@"<h2[^>]*id=""([^""]+)""[^>]*>", RegexOptions.IgnoreCase);
var h2Matches = h2Pattern.Matches(html);
Console.WriteLine($"\n<h2 id=...> elements found: {h2Matches.Count}");

// List first 10 h2 ids
foreach (var m in h2Matches.Take(15))
{
    Console.WriteLine($"  h2 id=\"{m.Groups[1].Value}\"");
}

// Check for table elements near webhook sections
var tablePattern = new Regex(@"<table[^>]*>", RegexOptions.IgnoreCase);
var tableMatches = tablePattern.Matches(html);
Console.WriteLine($"\n<table> elements found: {tableMatches.Count}");

// Find a specific event section to understand structure
var branchProtIdx = html.IndexOf("branch_protection_rule", StringComparison.Ordinal);
if (branchProtIdx >= 0)
{
    // Print 2000 chars around first mention
    var start = Math.Max(0, branchProtIdx - 200);
    var end = Math.Min(html.Length, branchProtIdx + 2000);
    Console.WriteLine($"\n=== Snippet around 'branch_protection_rule' (pos {branchProtIdx}) ===");
    Console.WriteLine(html[start..end]);
}
else
{
    Console.WriteLine("\n'branch_protection_rule' NOT found in HTML - page may be SPA/JS-rendered");
}

// Also check for "push" event payload section with properties
var pushPayloadIdx = html.IndexOf("Webhook payload object for", StringComparison.Ordinal);
if (pushPayloadIdx >= 0)
{
    var start = Math.Max(0, pushPayloadIdx - 100);
    var end = Math.Min(html.Length, pushPayloadIdx + 1500);
    Console.WriteLine($"\n=== Snippet around 'Webhook payload object for' (pos {pushPayloadIdx}) ===");
    Console.WriteLine(html[start..end]);
}
else
{
    Console.WriteLine("\n'Webhook payload object for' NOT found - page content is JS-rendered");
}

// Check for JSON-LD or embedded data
var nextDataIdx = html.IndexOf("__NEXT_DATA__", StringComparison.Ordinal);
if (nextDataIdx >= 0)
{
    Console.WriteLine($"\n__NEXT_DATA__ found at pos {nextDataIdx} - Next.js SSR, data may be in script tag");
    var scriptStart = html.LastIndexOf("<script", nextDataIdx, StringComparison.Ordinal);
    var scriptEnd = html.IndexOf("</script>", nextDataIdx, StringComparison.Ordinal);
    if (scriptStart >= 0 && scriptEnd >= 0)
    {
        var scriptLen = scriptEnd - scriptStart;
        Console.WriteLine($"  Script tag length: {scriptLen} chars");
        // Show first 500 chars of the script content
        var preview = html.Substring(scriptStart, Math.Min(500, scriptLen));
        Console.WriteLine($"  Preview: {preview}...");
    }
}

Console.WriteLine("\nDone.");
