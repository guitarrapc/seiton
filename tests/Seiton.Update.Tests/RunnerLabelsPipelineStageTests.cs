using System.Text.Json;
using Seiton.Update.Sources;

namespace Seiton.Update.Tests;

public sealed class RunnerLabelsPipelineStageTests
{
    [Test]
    public async Task MergeParsedSources_IncludesDeprecatedLabelsFromCuratedFile()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithParsed(repoRoot);

        try
        {
            var fetcher = new GitHubRunnerLabelsFetcher();
            fetcher.MergeParsedSources(tempRepo);

            var snapshotPath = Path.Combine(tempRepo, "data", "sources", "runner-labels", "github", "runner_labels.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(snapshotPath));

            await Assert.That(doc.RootElement.TryGetProperty("deprecatedLabels", out var deprecatedElement)).IsTrue();
            var deprecated = deprecatedElement
                .EnumerateArray()
                .Select(static x => x.GetString())
                .ToArray();

            await Assert.That(deprecated).IsEqualTo(Array.Empty<string>());
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    private static string CreateTempRepoWithParsed(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-runner-labels-tests-" + Guid.NewGuid().ToString("N"));
        var baseDir = Path.Combine(tempRepo, "data", "sources", "runner-labels", "github");
        var parsedDir = Path.Combine(baseDir, "parsed");
        Directory.CreateDirectory(parsedDir);

        File.Copy(
            Path.Combine(repoRoot, "data", "sources", "runner-labels", "github", "parsed", "docs-runner-labels.json"),
            Path.Combine(parsedDir, "docs-runner-labels.json"),
            overwrite: true);
        File.Copy(
            Path.Combine(repoRoot, "data", "sources", "runner-labels", "github", "supplemental-labels.json"),
            Path.Combine(baseDir, "supplemental-labels.json"),
            overwrite: true);
        File.Copy(
            Path.Combine(repoRoot, "data", "sources", "runner-labels", "github", "deprecated-labels.json"),
            Path.Combine(baseDir, "deprecated-labels.json"),
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
