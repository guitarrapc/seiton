using System.Text.Json;
using Seiton.Update.Sources;

namespace Seiton.Update.Tests;

public sealed class AvailabilityPipelineStageTests
{
    [Test]
    public async Task ParseLocalSourceFiles_ProducesOutputMatchingCommittedParsedFile()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var fetcher = new GitHubAvailabilityFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var actual = File.ReadAllText(
                Path.Combine(tempRepo, "data", "sources", "availability", "github", "parsed", "docs-context-availability.json"))
                .Replace("\r\n", "\n");

            var expected = File.ReadAllText(
                Path.Combine(repoRoot, "data", "sources", "availability", "github", "parsed", "docs-context-availability.json"))
                .Replace("\r\n", "\n");

            await Assert.That(actual).IsEqualTo(expected);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ParseLocalSourceFiles_ParsedOutput_ContainsRequiredWorkflowKeys()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var fetcher = new GitHubAvailabilityFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var path = Path.Combine(tempRepo, "data", "sources", "availability", "github", "parsed", "docs-context-availability.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var keys = doc.RootElement.GetProperty("entries")
                .EnumerateArray()
                .Select(x => x.GetProperty("workflowKey").GetString())
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);

            await Assert.That(keys).Contains("run-name");
            await Assert.That(keys).Contains("jobs.<job_id>.concurrency");
            await Assert.That(keys).Contains("jobs.<job_id>.steps.run");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task MergeParsedSources_ProducesOutputMatchingCommittedSnapshot()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var fetcher = new GitHubAvailabilityFetcher();
            fetcher.MergeParsedSources(tempRepo);

            var actual = File.ReadAllText(
                Path.Combine(tempRepo, "data", "sources", "availability", "github", "availability.json"))
                .Replace("\r\n", "\n");

            var expected = File.ReadAllText(
                Path.Combine(repoRoot, "data", "sources", "availability", "github", "availability.json"))
                .Replace("\r\n", "\n");

            await Assert.That(actual).IsEqualTo(expected);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task MergeParsedSources_CanonicalEntries_MatchExpectedContexts()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var fetcher = new GitHubAvailabilityFetcher();
            fetcher.MergeParsedSources(tempRepo);

            var path = Path.Combine(tempRepo, "data", "sources", "availability", "github", "availability.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            var entries = doc.RootElement.GetProperty("entries").EnumerateArray()
                .ToDictionary(
                    e => e.GetProperty("workflowKey").GetString()!,
                    e => e.GetProperty("contexts").EnumerateArray().Select(c => c.GetString()!).ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal);

            // Workflow-level: concurrency has github, inputs, vars
            await Assert.That(entries).ContainsKey("concurrency");
            await Assert.That(entries["concurrency"]).Contains("github");
            await Assert.That(entries["concurrency"]).Contains("inputs");
            await Assert.That(entries["concurrency"]).Contains("vars");

            // WorkflowCall output: has jobs context
            await Assert.That(entries).ContainsKey("on.workflow_call.outputs.<output_id>.value");
            await Assert.That(entries["on.workflow_call.outputs.<output_id>.value"]).Contains("jobs");

            // Job-level: env has needs, strategy, matrix, secrets
            await Assert.That(entries).ContainsKey("jobs.<job_id>.env");
            await Assert.That(entries["jobs.<job_id>.env"]).Contains("needs");
            await Assert.That(entries["jobs.<job_id>.env"]).Contains("strategy");
            await Assert.That(entries["jobs.<job_id>.env"]).Contains("matrix");
            await Assert.That(entries["jobs.<job_id>.env"]).Contains("secrets");

            // Job outputs: has steps, runner, job
            await Assert.That(entries).ContainsKey("jobs.<job_id>.outputs.<output_id>");
            await Assert.That(entries["jobs.<job_id>.outputs.<output_id>"]).Contains("steps");
            await Assert.That(entries["jobs.<job_id>.outputs.<output_id>"]).Contains("runner");
            await Assert.That(entries["jobs.<job_id>.outputs.<output_id>"]).Contains("job");

            // Strategy: has github, needs, vars, inputs but NOT runner/strategy/matrix
            await Assert.That(entries).ContainsKey("jobs.<job_id>.strategy");
            await Assert.That(entries["jobs.<job_id>.strategy"]).Contains("github");
            await Assert.That(entries["jobs.<job_id>.strategy"]).Contains("needs");
            await Assert.That(entries["jobs.<job_id>.strategy"]).DoesNotContain("runner");
            await Assert.That(entries["jobs.<job_id>.strategy"]).DoesNotContain("strategy");
            await Assert.That(entries["jobs.<job_id>.strategy"]).DoesNotContain("matrix");

            // Step-level: run has job, runner, secrets, steps
            await Assert.That(entries).ContainsKey("jobs.<job_id>.steps.run");
            await Assert.That(entries["jobs.<job_id>.steps.run"]).Contains("job");
            await Assert.That(entries["jobs.<job_id>.steps.run"]).Contains("runner");
            await Assert.That(entries["jobs.<job_id>.steps.run"]).Contains("secrets");
            await Assert.That(entries["jobs.<job_id>.steps.run"]).Contains("steps");

            // Reusable workflow call secrets: has secrets
            await Assert.That(entries).ContainsKey("jobs.<job_id>.secrets.<secrets_id>");
            await Assert.That(entries["jobs.<job_id>.secrets.<secrets_id>"]).Contains("secrets");
            await Assert.That(entries["jobs.<job_id>.secrets.<secrets_id>"]).Contains("needs");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    private static string CreateTempRepoWithRaw(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-update-tests-" + Guid.NewGuid().ToString("N"));
        var srcRaw = Path.Combine(repoRoot, "data", "sources", "availability", "github", "raw");
        var dstRaw = Path.Combine(tempRepo, "data", "sources", "availability", "github", "raw");
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
        var srcParsed = Path.Combine(repoRoot, "data", "sources", "availability", "github", "parsed");
        var dstParsed = Path.Combine(tempRepo, "data", "sources", "availability", "github", "parsed");
        Directory.CreateDirectory(dstParsed);

        foreach (var file in Directory.GetFiles(srcParsed))
        {
            File.Copy(file, Path.Combine(dstParsed, Path.GetFileName(file)));
        }

        return tempRepo;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var slnxPath = Path.Combine(dir.FullName, "seiton.slnx");
            if (File.Exists(slnxPath))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found from test base directory.");
    }
}
