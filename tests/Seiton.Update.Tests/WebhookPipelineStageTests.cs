using System.Text.Json;
using Seiton.Update.Sources;

namespace Seiton.Update.Tests;

/// <summary>
/// Integration tests for Stage 2 (Parse) and Stage 3 (Merge) of the webhook pipeline.
/// These tests operate exclusively on local files — no network access.
/// They verify that parse/merge logic is deterministic and produces the expected outputs
/// given the raw source files already committed to the repository.
/// </summary>
public sealed class WebhookPipelineStageTests
{
    // Stage 2: ParseLocalSourceFiles

    [Test]
    public async Task ParseLocalSourceFiles_ProducesOutputMatchingCommittedParsedFiles()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var fetcher = new GitHubWebhookFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var parsedDir = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "parsed");
            var actualSchema = File.ReadAllText(Path.Combine(parsedDir, "schema-webhook-events.json")).Replace("\r\n", "\n");
            var actualDocs = File.ReadAllText(Path.Combine(parsedDir, "docs-webhook-events.json")).Replace("\r\n", "\n");

            var committedDir = Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "parsed");
            var expectedSchema = File.ReadAllText(Path.Combine(committedDir, "schema-webhook-events.json")).Replace("\r\n", "\n");
            var expectedDocs = File.ReadAllText(Path.Combine(committedDir, "docs-webhook-events.json")).Replace("\r\n", "\n");

            await Assert.That(actualSchema).IsEqualTo(expectedSchema);
            await Assert.That(actualDocs).IsEqualTo(expectedDocs);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ParseLocalSourceFiles_SchemaSnapshot_ContainsExpectedEventNames()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var fetcher = new GitHubWebhookFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var parsedSchemaPath = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "parsed", "schema-webhook-events.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(parsedSchemaPath));
            var eventNames = doc.RootElement
                .GetProperty("events")
                .EnumerateArray()
                .Select(e => e.GetProperty("name").GetString())
                .Where(n => n is not null)
                .ToHashSet(StringComparer.Ordinal);

            // Core webhook events that must come from the schema
            await Assert.That(eventNames).Contains("push");
            await Assert.That(eventNames).Contains("pull_request");
            await Assert.That(eventNames).Contains("check_suite");
            await Assert.That(eventNames).Contains("issues");
            await Assert.That(eventNames).Contains("repository_dispatch");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ParseLocalSourceFiles_DocsSnapshot_PullRequestMarkedAsUnparseable()
    {
        // pull_request uses Liquid template cells; it should be docs-known but hasParseableActivityTypes=false
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var fetcher = new GitHubWebhookFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var parsedDocsPath = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "parsed", "docs-webhook-events.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(parsedDocsPath));
            var prEvent = doc.RootElement
                .GetProperty("events")
                .EnumerateArray()
                .FirstOrDefault(e => e.GetProperty("name").GetString() == "pull_request");

            await Assert.That(prEvent.ValueKind).IsNotEqualTo(JsonValueKind.Undefined);
            await Assert.That(prEvent.GetProperty("hasParseableActivityTypes").GetBoolean()).IsFalse();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ParseLocalSourceFiles_DocsSnapshot_CheckSuiteMarkedAsParseable()
    {
        // check_suite has a plain table in Docs (no Liquid); should be parseable
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var fetcher = new GitHubWebhookFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var parsedDocsPath = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "parsed", "docs-webhook-events.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(parsedDocsPath));
            var csEvent = doc.RootElement
                .GetProperty("events")
                .EnumerateArray()
                .FirstOrDefault(e => e.GetProperty("name").GetString() == "check_suite");

            await Assert.That(csEvent.ValueKind).IsNotEqualTo(JsonValueKind.Undefined);
            await Assert.That(csEvent.GetProperty("hasParseableActivityTypes").GetBoolean()).IsTrue();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ParseLocalSourceFiles_MissingRawFiles_ThrowsFileNotFoundException()
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-update-tests-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRepo);

        try
        {
            var fetcher = new GitHubWebhookFetcher();
            await Assert.That(() => fetcher.ParseLocalSourceFiles(tempRepo)).ThrowsException();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    // Stage 3: MergeParsedSources

    [Test]
    public async Task MergeParsedSources_ProducesOutputMatchingCommittedSnapshot()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var fetcher = new GitHubWebhookFetcher();
            fetcher.MergeParsedSources(tempRepo);

            var actual = File.ReadAllText(
                Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "webhook_types.json"))
                .Replace("\r\n", "\n");

            var expected = File.ReadAllText(
                Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "webhook_types.json"))
                .Replace("\r\n", "\n");

            await Assert.That(actual).IsEqualTo(expected);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task MergeParsedSources_DefaultIncludesSchemaOnlyEvents()
    {
        // project, project_card, project_column exist only in schema (not in Docs)
        // They must be present when excludeSchemaOnly=false (default)
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var fetcher = new GitHubWebhookFetcher();
            fetcher.MergeParsedSources(tempRepo, excludeSchemaOnly: false);

            var snapshotPath = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "webhook_types.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(snapshotPath));
            var names = doc.RootElement
                .GetProperty("events")
                .EnumerateArray()
                .Select(e => e.GetProperty("name").GetString())
                .ToHashSet(StringComparer.Ordinal);

            await Assert.That(names).Contains("project");
            await Assert.That(names).Contains("project_card");
            await Assert.That(names).Contains("project_column");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task MergeParsedSources_ExcludeSchemaOnly_OmitsSchemaOnlyEvents()
    {
        // Use synthetic parsed fixtures: inject a fake "schema_only_event" that is absent from Docs.
        // When excludeSchemaOnly=true it must be omitted; when false, it must appear.
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            // Append a schema-only entry to the parsed schema file.
            InjectSchemaOnlyEvent(tempRepo, "fake_schema_only_event");

            var fetcher = new GitHubWebhookFetcher();
            fetcher.MergeParsedSources(tempRepo, excludeSchemaOnly: true);

            var snapshotPath = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "webhook_types.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(snapshotPath));
            var names = doc.RootElement
                .GetProperty("events")
                .EnumerateArray()
                .Select(e => e.GetProperty("name").GetString())
                .ToHashSet(StringComparer.Ordinal);

            await Assert.That(names).DoesNotContain("fake_schema_only_event");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task MergeParsedSources_ExcludeSchemaOnly_DocsKnownEventsStillPresent()
    {
        // pull_request is docs-known (heading) even though its table is unparseable (Liquid).
        // It must survive excludeSchemaOnly because it is recognized by Docs.
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var fetcher = new GitHubWebhookFetcher();
            fetcher.MergeParsedSources(tempRepo, excludeSchemaOnly: true);

            var snapshotPath = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "webhook_types.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(snapshotPath));
            var names = doc.RootElement
                .GetProperty("events")
                .EnumerateArray()
                .Select(e => e.GetProperty("name").GetString())
                .ToHashSet(StringComparer.Ordinal);

            await Assert.That(names).Contains("pull_request");
            await Assert.That(names).Contains("pull_request_target");
            await Assert.That(names).Contains("push");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task MergeParsedSources_CheckSuiteFollowsDocsNotSchema()
    {
        // Docs has check_suite = [completed]; schema adds requested/rerequested.
        // Merged output must follow Docs: only [completed].
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var fetcher = new GitHubWebhookFetcher();
            fetcher.MergeParsedSources(tempRepo);

            var snapshotPath = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "webhook_types.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(snapshotPath));
            var csEvent = doc.RootElement
                .GetProperty("events")
                .EnumerateArray()
                .FirstOrDefault(e => e.GetProperty("name").GetString() == "check_suite");

            await Assert.That(csEvent.ValueKind).IsNotEqualTo(JsonValueKind.Undefined);
            var types = csEvent.GetProperty("activityTypes")
                .EnumerateArray()
                .Select(t => t.GetString())
                .ToArray();

            await Assert.That(types).Contains("completed");
            await Assert.That(types).DoesNotContain("requested");
            await Assert.That(types).DoesNotContain("rerequested");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task MergeParsedSources_IsIdempotent()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var fetcher = new GitHubWebhookFetcher();
            fetcher.MergeParsedSources(tempRepo);
            var firstRun = File.ReadAllText(
                Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "webhook_types.json"));

            fetcher.MergeParsedSources(tempRepo);
            var secondRun = File.ReadAllText(
                Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "webhook_types.json"));

            await Assert.That(secondRun).IsEqualTo(firstRun);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task MergeParsedSources_MissingParsedFiles_ThrowsFileNotFoundException()
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-update-tests-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRepo);

        try
        {
            var fetcher = new GitHubWebhookFetcher();
            await Assert.That(() => fetcher.MergeParsedSources(tempRepo)).ThrowsException();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    // Helpers

    [Test]
    public async Task MergeParsedSources_DefaultMode_IncludesInjectedSchemaOnlyEvent()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            InjectSchemaOnlyEvent(tempRepo, "fake_schema_only_event");

            var fetcher = new GitHubWebhookFetcher();
            fetcher.MergeParsedSources(tempRepo, excludeSchemaOnly: false);

            var snapshotPath = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "webhook_types.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(snapshotPath));
            var names = doc.RootElement
                .GetProperty("events")
                .EnumerateArray()
                .Select(e => e.GetProperty("name").GetString())
                .ToHashSet(StringComparer.Ordinal);

            await Assert.That(names).Contains("fake_schema_only_event");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    private static string CreateTempRepoWithRaw(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-update-tests-" + Guid.NewGuid().ToString("N"));
        var srcRaw = Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "raw");
        var dstRaw = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "raw");
        Directory.CreateDirectory(dstRaw);

        foreach (var file in Directory.GetFiles(srcRaw))
        {
            File.Copy(file, Path.Combine(dstRaw, Path.GetFileName(file)));
        }

        return tempRepo;
    }

    private static string CreateTempRepoWithParsed(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-update-tests-" + Guid.NewGuid().ToString("N"));
        var srcParsed = Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "parsed");
        var dstParsed = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "parsed");
        Directory.CreateDirectory(dstParsed);

        foreach (var file in Directory.GetFiles(srcParsed))
        {
            File.Copy(file, Path.Combine(dstParsed, Path.GetFileName(file)));
        }

        // Also need the reports dir for the diff report output
        Directory.CreateDirectory(Path.Combine(tempRepo, "data", "sources", "reports"));

        return tempRepo;
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

    /// <summary>
    /// Appends a schema-only event (absent from docs-webhook-events.json) to the parsed schema
    /// snapshot in a temp repo. Used to test excludeSchemaOnly logic without relying on real
    /// schema-only events (which may not exist if sources stay in sync).
    /// </summary>
    private static void InjectSchemaOnlyEvent(string tempRepo, string eventName)
    {
        var parsedSchemaPath = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "parsed", "schema-webhook-events.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(parsedSchemaPath));

        var events = doc.RootElement.GetProperty("events").EnumerateArray()
            .Select(e => new { name = e.GetProperty("name").GetString()!, activityTypes = (object?)null })
            .Append(new { name = eventName, activityTypes = (object?)null })
            .OrderBy(e => e.name, StringComparer.Ordinal)
            .ToArray();

        var updated = new
        {
            schemaVersion = doc.RootElement.GetProperty("schemaVersion").GetInt32(),
            source = doc.RootElement.GetProperty("source").GetString()!,
            events,
        };

        var json = JsonSerializer.Serialize(updated, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        File.WriteAllText(parsedSchemaPath, json.Replace("\r\n", "\n"));
    }
}
