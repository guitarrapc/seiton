using System.Text.Json;
using Seiton.Update.Sources;

namespace Seiton.Update.Tests;

public sealed class StepSchemaPipelineStageTests
{
    [Test]
    public async Task ParseLocalSourceFiles_WritesArtifactUnderParsedDirectory()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRawOnly(repoRoot);

        try
        {
            var fetcher = new GitHubStepSchemaFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);

            var parsedPath = Path.Combine(
                tempRepo, "data", "sources", "step-schema", "github", "parsed", "step-schema.json");
            await Assert.That(File.Exists(parsedPath)).IsTrue();

            using var doc = JsonDocument.Parse(File.ReadAllText(parsedPath));
            await Assert.That(doc.RootElement.TryGetProperty("forms", out var forms)).IsTrue();
            await Assert.That(forms.GetArrayLength()).IsEqualTo(2);
            await Assert.That(doc.RootElement.TryGetProperty("properties", out _)).IsTrue();
            await Assert.That(doc.RootElement.TryGetProperty("modifiers", out var modifiers)).IsFalse();
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
            var fetcher = new GitHubStepSchemaFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);
            fetcher.MergeParsedSources(tempRepo);

            var actual = File.ReadAllText(
                    Path.Combine(tempRepo, "data", "sources", "step-schema", "github", "step-schema.json"))
                .Replace("\r\n", "\n");

            var expected = File.ReadAllText(
                    Path.Combine(repoRoot, "data", "sources", "step-schema", "github", "step-schema.json"))
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
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-ss-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRepo, "data", "sources", "step-schema", "github", "parsed"));
            File.WriteAllText(
                Path.Combine(tempRepo, "data", "sources", "step-schema", "github", "supplemental-step-schema.json"),
                File.ReadAllText(Path.Combine(
                    FindRepoRoot(),
                    "data",
                    "sources",
                    "step-schema",
                    "github",
                    "supplemental-step-schema.json")));

            Assert.Throws<FileNotFoundException>(() =>
                new GitHubStepSchemaFetcher().MergeParsedSources(tempRepo));
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
        var srcGithubDir = Path.Combine(repoRoot, "data", "sources", "step-schema", "github");
        var dstGithubDir = Path.Combine(tempRepo, "data", "sources", "step-schema", "github");
        var dstRawDir = Path.Combine(dstGithubDir, "raw");
        Directory.CreateDirectory(dstRawDir);

        foreach (var file in Directory.GetFiles(Path.Combine(srcGithubDir, "raw")))
        {
            File.Copy(file, Path.Combine(dstRawDir, Path.GetFileName(file)));
        }

        File.Copy(
            Path.Combine(srcGithubDir, "supplemental-step-schema.json"),
            Path.Combine(dstGithubDir, "supplemental-step-schema.json"));

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
