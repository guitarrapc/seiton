using System.Text.Json;
using Seiton.Update.Sources;

namespace Seiton.Update.Tests;

public sealed class PopularActionsPipelineStageTests
{
    [Test]
    public async Task ParseLocalSourceFiles_ProducesOutputMatchingCommittedParsedFile()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRaw(repoRoot);

        try
        {
            var fetcher = new GitHubPopularActionsFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var actual = File.ReadAllText(
                Path.Combine(tempRepo, "data", "sources", "popular-actions", "github", "parsed", "popular-actions-metadata.json"))
                .Replace("\r\n", "\n");

            var expected = File.ReadAllText(
                Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "parsed", "popular-actions-metadata.json"))
                .Replace("\r\n", "\n");

            await Assert.That(actual).IsEqualTo(expected);
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
            var fetcher = new GitHubPopularActionsFetcher();
            fetcher.MergeParsedSources(tempRepo);

            var actual = File.ReadAllText(
                Path.Combine(tempRepo, "data", "sources", "popular-actions", "github", "popular_actions.json"))
                .Replace("\r\n", "\n");

            var expected = File.ReadAllText(
                Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "popular_actions.json"))
                .Replace("\r\n", "\n");

            await Assert.That(actual).IsEqualTo(expected);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task MergeParsedSources_SnapshotContainsKnownActionAndInputs()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var fetcher = new GitHubPopularActionsFetcher();
            fetcher.MergeParsedSources(tempRepo);

            var path = Path.Combine(tempRepo, "data", "sources", "popular-actions", "github", "popular_actions.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            var checkout = doc.RootElement
                .GetProperty("actions")
                .EnumerateArray()
                .FirstOrDefault(x => x.GetProperty("uses").GetString() == "actions/checkout");

            await Assert.That(checkout.ValueKind).IsNotEqualTo(JsonValueKind.Undefined);

            var inputNames = checkout.GetProperty("inputs")
                .EnumerateArray()
                .Select(x => x.GetString())
                .ToHashSet(StringComparer.Ordinal);

            await Assert.That(inputNames).Contains("fetch-depth");
            await Assert.That(inputNames).Contains("repository");
            await Assert.That(inputNames).Contains("token");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    static string CreateTempRepoWithRaw(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-update-tests-" + Guid.NewGuid().ToString("N"));
        var srcRaw = Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "raw");
        var dstRaw = Path.Combine(tempRepo, "data", "sources", "popular-actions", "github", "raw");
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
        var srcParsed = Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "parsed");
        var dstParsed = Path.Combine(tempRepo, "data", "sources", "popular-actions", "github", "parsed");
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
