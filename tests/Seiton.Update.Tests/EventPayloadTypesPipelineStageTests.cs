using System.Text.Json;
using Seiton.Update.Services;
using Seiton.Update.Sources;

namespace Seiton.Update.Tests;

public sealed class EventPayloadTypesPipelineStageTests
{
    [Test]
    public async Task ParseLocalSourceFiles_WritesParsedJsonWithEvents()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRawOnly(repoRoot);

        try
        {
            var fetcher = new GitHubEventPayloadTypesFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var parsedPath = Path.Combine(
                tempRepo, "data", "sources", "webhooks", "github", "parsed", "parsed-event-payload-types.json");
            await Assert.That(File.Exists(parsedPath)).IsTrue();

            using var doc = JsonDocument.Parse(File.ReadAllText(parsedPath));
            await Assert.That(doc.RootElement.TryGetProperty("events", out var events)).IsTrue();
            await Assert.That(events.GetArrayLength()).IsGreaterThan(10);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ParseFromCommittedRaw_PrimaryMatchesCommittedSemantics()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRawOnly(repoRoot);

        try
        {
            new GitHubEventPayloadTypesFetcher().ParseLocalSourceFiles(tempRepo);

            var primaryPath = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "event_payload_types.json");
            var committedPrimaryPath = Path.Combine(
                repoRoot, "data", "sources", "webhooks", "github", "event_payload_types.json");
            var rawPath = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "raw", "webhook-events-and-payloads.html");

            using var actual = JsonDocument.Parse(File.ReadAllText(primaryPath));
            using var expected = JsonDocument.Parse(File.ReadAllText(committedPrimaryPath));

            await Assert.That(actual.RootElement.GetProperty("schemaVersion").GetInt32())
                .IsEqualTo(expected.RootElement.GetProperty("schemaVersion").GetInt32());
            await Assert.That(actual.RootElement.GetProperty("source").GetString())
                .IsEqualTo(expected.RootElement.GetProperty("source").GetString());

            var rawText = File.ReadAllText(rawPath);
            var expectedRawSha = SourceContentHasher.ComputeSha256(rawText);
            var actualSha = actual.RootElement.GetProperty("rawSources")[0].GetProperty("sha256").GetString();
            await Assert.That(actualSha).IsEqualTo(expectedRawSha);

            var actualEvents = actual.RootElement.GetProperty("events");
            var expectedEvents = expected.RootElement.GetProperty("events");
            await Assert.That(actualEvents.GetArrayLength()).IsEqualTo(expectedEvents.GetArrayLength());

            await Assert.That(actualEvents[0].GetProperty("name").GetString())
                .IsEqualTo(expectedEvents[0].GetProperty("name").GetString());
            var n = actualEvents.GetArrayLength();
            await Assert.That(actualEvents[n - 1].GetProperty("name").GetString())
                .IsEqualTo(expectedEvents[n - 1].GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    private static string CreateTempRepoWithRawOnly(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-ept-" + Guid.NewGuid().ToString("N"));
        var srcRawDir = Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "raw");
        var dstRawDir = Path.Combine(tempRepo, "data", "sources", "webhooks", "github", "raw");
        Directory.CreateDirectory(dstRawDir);

        var htmlName = "webhook-events-and-payloads.html";
        File.Copy(
            Path.Combine(srcRawDir, htmlName),
            Path.Combine(dstRawDir, htmlName),
            overwrite: true);

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
}
