using System.Text.Json;
using Seiton.Update.Sources;

namespace Seiton.Update.Tests;

public sealed class ShellsPipelineStageTests
{
    [Test]
    public async Task ParseLocalSourceFiles_WritesArtifactUnderParsedDirectory()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRawOnly(repoRoot);

        try
        {
            var fetcher = new GitHubShellsFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var parsedPath = Path.Combine(
                tempRepo, "data", "sources", "shells", "github", "parsed", "shells.json");
            await Assert.That(File.Exists(parsedPath)).IsTrue();

            using var doc = JsonDocument.Parse(File.ReadAllText(parsedPath));
            await Assert.That(doc.RootElement.TryGetProperty("shells", out var shells)).IsTrue();
            await Assert.That(shells.GetArrayLength()).IsEqualTo(6);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task ParseThenMerge_ProducesPrimaryMatchingCommittedSnapshot()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRawOnly(repoRoot);

        try
        {
            var fetcher = new GitHubShellsFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);
            fetcher.MergeParsedSources(tempRepo);

            var actual = File.ReadAllText(
                    Path.Combine(tempRepo, "data", "sources", "shells", "github", "shells.json"))
                .Replace("\r\n", "\n");

            var expected = File.ReadAllText(
                    Path.Combine(repoRoot, "data", "sources", "shells", "github", "shells.json"))
                .Replace("\r\n", "\n");

            await Assert.That(actual).IsEqualTo(expected);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public void MergeParsedSources_WhenParsedMissing_Throws()
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-sh-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRepo, "data", "sources", "shells", "github", "parsed"));

            Assert.Throws<FileNotFoundException>(() =>
                new GitHubShellsFetcher().MergeParsedSources(tempRepo));
        }
        finally
        {
            if (Directory.Exists(tempRepo))
            {
                Directory.Delete(tempRepo, recursive: true);
            }
        }
    }

    private static string CreateTempRepoWithRawOnly(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-update-tests-" + Guid.NewGuid().ToString("N"));
        var srcRawDir = Path.Combine(repoRoot, "data", "sources", "shells", "github", "raw");
        var dstRawDir = Path.Combine(tempRepo, "data", "sources", "shells", "github", "raw");
        Directory.CreateDirectory(dstRawDir);

        foreach (var file in Directory.GetFiles(srcRawDir))
        {
            File.Copy(file, Path.Combine(dstRawDir, Path.GetFileName(file)));
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
