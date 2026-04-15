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
    public async Task MergeParsedSources_CanonicalRoots_MatchExpectedSets()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var fetcher = new GitHubAvailabilityFetcher();
            fetcher.MergeParsedSources(tempRepo);

            var path = Path.Combine(tempRepo, "data", "sources", "availability", "github", "availability.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            var workflowRoots = doc.RootElement.GetProperty("workflowRoots").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
            var jobRoots = doc.RootElement.GetProperty("jobRoots").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
            var stepRoots = doc.RootElement.GetProperty("stepRoots").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);

            await Assert.That(workflowRoots).Contains("github");
            await Assert.That(workflowRoots).Contains("inputs");
            await Assert.That(workflowRoots).Contains("vars");

            await Assert.That(jobRoots).Contains("needs");
            await Assert.That(jobRoots).Contains("strategy");
            await Assert.That(jobRoots).Contains("matrix");

            await Assert.That(stepRoots).Contains("job");
            await Assert.That(stepRoots).Contains("runner");
            await Assert.That(stepRoots).Contains("secrets");
            await Assert.That(stepRoots).Contains("steps");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    static string CreateTempRepoWithRaw(string repoRoot)
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

    static string CreateTempRepoWithParsed(string repoRoot)
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

    static string FindRepoRoot()
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
