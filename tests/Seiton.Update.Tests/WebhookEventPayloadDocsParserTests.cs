using Seiton.Update.Parsers;

namespace Seiton.Update.Tests;

/// <summary>
/// Unit tests for <see cref="WebhookEventPayloadDocsParser"/> covering JSON extraction,
/// type mapping, body parameter parsing, and full parse integration.
/// </summary>
public sealed class WebhookEventPayloadDocsParserTests
{
    // ExtractNextDataJson

    [Test]
    public async Task ExtractNextDataJson_ExtractsJsonFromScriptTag()
    {
        var html = """
                   <html>
                   <head></head>
                   <body>
                   <div>content</div>
                   <script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"webhooks":[]}}}</script>
                   </body>
                   </html>
                   """;

        var json = WebhookEventPayloadDocsParser.ExtractNextDataJson(html);

        await Assert.That(json).IsEqualTo("""{"props":{"pageProps":{"webhooks":[]}}}""");
    }

    [Test]
    public void ExtractNextDataJson_ThrowsWhenScriptTagMissing()
    {
        var html = "<html><body>no data</body></html>";

        Assert.Throws<InvalidDataException>(() => WebhookEventPayloadDocsParser.ExtractNextDataJson(html));
    }

    // MapType

    [Test]
    public async Task MapType_String()
    {
        var (type, elementType) = WebhookEventPayloadDocsParser.MapType("string");
        await Assert.That(type).IsEqualTo("string");
        await Assert.That(elementType).IsNull();
    }

    [Test]
    public async Task MapType_StringOrNull()
    {
        var (type, elementType) = WebhookEventPayloadDocsParser.MapType("string or null");
        await Assert.That(type).IsEqualTo("string");
        await Assert.That(elementType).IsNull();
    }

    [Test]
    public async Task MapType_Object()
    {
        var (type, elementType) = WebhookEventPayloadDocsParser.MapType("object");
        await Assert.That(type).IsEqualTo("object");
        await Assert.That(elementType).IsNull();
    }

    [Test]
    public async Task MapType_ObjectOrNull()
    {
        var (type, elementType) = WebhookEventPayloadDocsParser.MapType("object or null");
        await Assert.That(type).IsEqualTo("object");
        await Assert.That(elementType).IsNull();
    }

    [Test]
    public async Task MapType_Boolean()
    {
        var (type, elementType) = WebhookEventPayloadDocsParser.MapType("boolean");
        await Assert.That(type).IsEqualTo("bool");
        await Assert.That(elementType).IsNull();
    }

    [Test]
    public async Task MapType_Integer()
    {
        var (type, elementType) = WebhookEventPayloadDocsParser.MapType("integer");
        await Assert.That(type).IsEqualTo("number");
        await Assert.That(elementType).IsNull();
    }

    [Test]
    public async Task MapType_ArrayOfObjects()
    {
        var (type, elementType) = WebhookEventPayloadDocsParser.MapType("array of objects");
        await Assert.That(type).IsEqualTo("array");
        await Assert.That(elementType).IsEqualTo("object");
    }

    [Test]
    public async Task MapType_ArrayOfStrings()
    {
        var (type, elementType) = WebhookEventPayloadDocsParser.MapType("array of strings");
        await Assert.That(type).IsEqualTo("array");
        await Assert.That(elementType).IsEqualTo("string");
    }

    [Test]
    public async Task MapType_Unknown_ReturnsAny()
    {
        var (type, elementType) = WebhookEventPayloadDocsParser.MapType("something_unknown");
        await Assert.That(type).IsEqualTo("any");
        await Assert.That(elementType).IsNull();
    }

    // Full Parse integration with synthetic HTML

    [Test]
    public async Task Parse_SyntheticHtml_ExtractsEvents()
    {
        var html = BuildSyntheticHtml("""
            [
              {
                "name": "push",
                "actionTypes": [],
                "data": {
                  "descriptionHtml": "",
                  "summaryHtml": "",
                  "bodyParameters": [
                    { "name": "ref", "type": "string", "isRequired": true },
                    { "name": "after", "type": "string", "isRequired": true },
                    { "name": "before", "type": "string", "isRequired": true },
                    { "name": "commits", "type": "array of objects", "isRequired": true },
                    { "name": "head_commit", "type": "object or null" },
                    { "name": "repository", "type": "object", "isRequired": true },
                    { "name": "sender", "type": "object" }
                  ],
                  "availability": [],
                  "action": "",
                  "category": "push"
                }
              },
              {
                "name": "issues",
                "actionTypes": ["opened", "closed"],
                "data": {
                  "descriptionHtml": "",
                  "summaryHtml": "",
                  "bodyParameters": [
                    { "name": "action", "type": "string", "isRequired": true },
                    { "name": "issue", "type": "object", "isRequired": true },
                    { "name": "repository", "type": "object", "isRequired": true },
                    { "name": "sender", "type": "object", "isRequired": true }
                  ],
                  "availability": [],
                  "action": "opened",
                  "category": "issues"
                }
              }
            ]
            """);

        var parser = new WebhookEventPayloadDocsParser();
        var model = parser.Parse(html);

        // Should have push, issues, and supplemental events (schedule)
        await Assert.That(model.Events.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(model.Source).IsEqualTo("github-docs-webhook-events-and-payloads");

        // Check push event
        var push = model.Events.FirstOrDefault(e => e.Name == "push");
        await Assert.That(push).IsNotNull();
        await Assert.That(push!.Properties.Count).IsEqualTo(7);
        await Assert.That(push.Properties.Any(p => p.Name == "ref" && p.Type == "string")).IsTrue();
        await Assert.That(push.Properties.Any(p => p.Name == "commits" && p.Type == "array" && p.ElementType?.Type == "object")).IsTrue();

        // Check issues event
        var issues = model.Events.FirstOrDefault(e => e.Name == "issues");
        await Assert.That(issues).IsNotNull();
        await Assert.That(issues!.Properties.Any(p => p.Name == "action" && p.Type == "string")).IsTrue();
        await Assert.That(issues.Properties.Any(p => p.Name == "issue" && p.Type == "object")).IsTrue();

        // Check supplemental schedule event
        var schedule = model.Events.FirstOrDefault(e => e.Name == "schedule");
        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.Properties.Any(p => p.Name == "schedule" && p.Type == "string")).IsTrue();
    }

    [Test]
    public async Task Parse_SyntheticHtml_DerivesPullRequestTarget()
    {
        var html = BuildSyntheticHtml("""
            [
              {
                "name": "pull_request",
                "actionTypes": ["opened"],
                "data": {
                  "descriptionHtml": "",
                  "summaryHtml": "",
                  "bodyParameters": [
                    { "name": "action", "type": "string", "isRequired": true },
                    { "name": "number", "type": "integer", "isRequired": true },
                    { "name": "pull_request", "type": "object", "isRequired": true },
                    { "name": "repository", "type": "object", "isRequired": true },
                    { "name": "sender", "type": "object", "isRequired": true }
                  ],
                  "availability": [],
                  "action": "opened",
                  "category": "pull_request"
                }
              }
            ]
            """);

        var parser = new WebhookEventPayloadDocsParser();
        var model = parser.Parse(html);

        // pull_request_target should be derived from pull_request
        var prTarget = model.Events.FirstOrDefault(e => e.Name == "pull_request_target");
        await Assert.That(prTarget).IsNotNull();
        await Assert.That(prTarget!.Properties.Count).IsEqualTo(5);
        await Assert.That(prTarget.Properties.Any(p => p.Name == "pull_request")).IsTrue();
        await Assert.That(prTarget.Properties.Any(p => p.Name == "number" && p.Type == "number")).IsTrue();
    }

    [Test]
    public async Task Parse_SyntheticHtml_EventsSortedByName()
    {
        var html = BuildSyntheticHtml("""
            [
              {
                "name": "push",
                "actionTypes": [],
                "data": {
                  "descriptionHtml": "",
                  "summaryHtml": "",
                  "bodyParameters": [
                    { "name": "ref", "type": "string", "isRequired": true }
                  ],
                  "availability": [],
                  "action": "",
                  "category": "push"
                }
              },
              {
                "name": "create",
                "actionTypes": [],
                "data": {
                  "descriptionHtml": "",
                  "summaryHtml": "",
                  "bodyParameters": [
                    { "name": "ref", "type": "string", "isRequired": true }
                  ],
                  "availability": [],
                  "action": "",
                  "category": "create"
                }
              }
            ]
            """);

        var parser = new WebhookEventPayloadDocsParser();
        var model = parser.Parse(html);

        var names = model.Events.Select(e => e.Name).ToList();
        var sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
        await Assert.That(names).IsEquivalentTo(sorted);
    }

    // Contract test against committed raw HTML

    [Test]
    public async Task Parse_CommittedRawHtml_ExtractsComprehensiveEvents()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "data", "sources", "webhooks", "github", "raw",
            "webhook-events-and-payloads.html");

        if (!File.Exists(path))
        {
            // Skip if raw HTML not downloaded yet
            return;
        }

        var html = File.ReadAllText(path);
        var parser = new WebhookEventPayloadDocsParser();
        var model = parser.Parse(html);

        // Should have a comprehensive number of events (70+)
        await Assert.That(model.Events.Count).IsGreaterThanOrEqualTo(70);

        // Verify key workflow trigger events are present
        var eventNames = model.Events.Select(e => e.Name).ToHashSet();
        await Assert.That(eventNames).Contains("push");
        await Assert.That(eventNames).Contains("pull_request");
        await Assert.That(eventNames).Contains("pull_request_target");
        await Assert.That(eventNames).Contains("issues");
        await Assert.That(eventNames).Contains("workflow_dispatch");
        await Assert.That(eventNames).Contains("workflow_run");
        await Assert.That(eventNames).Contains("schedule");
        await Assert.That(eventNames).Contains("create");
        await Assert.That(eventNames).Contains("delete");
        await Assert.That(eventNames).Contains("release");

        // Verify push event has expected properties
        var push = model.Events.First(e => e.Name == "push");
        await Assert.That(push.Properties.Count).IsGreaterThanOrEqualTo(10);
        var pushPropNames = push.Properties.Select(p => p.Name).ToHashSet();
        await Assert.That(pushPropNames).Contains("ref");
        await Assert.That(pushPropNames).Contains("before");
        await Assert.That(pushPropNames).Contains("after");
        await Assert.That(pushPropNames).Contains("commits");
        await Assert.That(pushPropNames).Contains("repository");
        await Assert.That(pushPropNames).Contains("sender");

        // Verify push.commits is array of objects
        var commits = push.Properties.First(p => p.Name == "commits");
        await Assert.That(commits.Type).IsEqualTo("array");
        await Assert.That(commits.ElementType).IsNotNull();
        await Assert.That(commits.ElementType!.Type).IsEqualTo("object");

        // Verify schedule has synthetic payload
        var schedule = model.Events.First(e => e.Name == "schedule");
        await Assert.That(schedule.Properties.Count).IsEqualTo(1);
        await Assert.That(schedule.Properties[0].Name).IsEqualTo("schedule");
        await Assert.That(schedule.Properties[0].Type).IsEqualTo("string");
    }

    private static string BuildSyntheticHtml(string webhooksJsonArray)
    {
        return """<html><head></head><body><div>content</div><script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"webhooks":"""
            + webhooksJsonArray
            + """}}}</script></body></html>""";
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "seiton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found from test base directory.");
    }
}
