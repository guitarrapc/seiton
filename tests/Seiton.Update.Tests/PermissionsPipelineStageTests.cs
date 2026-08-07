using System.Text.Json;
using Seiton.Update.Sources;

namespace Seiton.Update.Tests;

public sealed class PermissionsPipelineStageTests
{
    [Test]
    public async Task ParseThenMerge_ProducesPrimaryMatchingCommittedSnapshot()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRawOnly(repoRoot);

        try
        {
            var fetcher = new GitHubPermissionsFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);
            fetcher.MergeParsedSources(tempRepo);

            var actual = File.ReadAllText(
                    Path.Combine(tempRepo, "data", "sources", "permissions", "github", "permissions.json"))
                .Replace("\r\n", "\n");

            var expected = File.ReadAllText(
                    Path.Combine(repoRoot, "data", "sources", "permissions", "github", "permissions.json"))
                .Replace("\r\n", "\n");

            await Assert.That(actual).IsEqualTo(expected);
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    /// <summary>
    /// The docs table dropped 'models' when GitHub Models was retired, but workflows declaring it
    /// still run. The merge stage must keep the scope so seiton does not report 'unknown permission scope'.
    /// </summary>
    [Test]
    public async Task MergeParsedSources_RetiredModelsScope_IsRetainedAsReadNone()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoWithRawOnly(repoRoot);

        try
        {
            var fetcher = new GitHubPermissionsFetcher();
            fetcher.ParseLocalSourceFiles(tempRepo);
            fetcher.MergeParsedSources(tempRepo);

            var parsedPath = Path.Combine(
                tempRepo, "data", "sources", "permissions", "github", "parsed", "permissions-scopes.json");
            using var parsedDoc = JsonDocument.Parse(File.ReadAllText(parsedPath));
            var parsedNames = parsedDoc.RootElement.GetProperty("scopes")
                .EnumerateArray()
                .Select(static s => s.GetProperty("name").GetString())
                .ToArray();

            // Precondition: the docs source no longer lists the scope.
            await Assert.That(parsedNames).DoesNotContain("models");

            var mergedPath = Path.Combine(
                tempRepo, "data", "sources", "permissions", "github", "permissions.json");
            using var mergedDoc = JsonDocument.Parse(File.ReadAllText(mergedPath));
            var models = mergedDoc.RootElement.GetProperty("scopes")
                .EnumerateArray()
                .Single(static s => s.GetProperty("name").GetString() == "models");

            var allowed = models.GetProperty("allowed")
                .EnumerateArray()
                .Select(static v => v.GetString()!)
                .ToArray();

            await Assert.That(allowed).IsEquivalentTo(new[] { "read", "none" });

            // The note drives the deprecated-permissions diagnostic message.
            var note = models.GetProperty("deprecationNote").GetString();
            await Assert.That(note).Contains("remove it from permissions");
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    private static string CreateTempRepoWithRawOnly(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-perm-tests-" + Guid.NewGuid().ToString("N"));
        var srcRawDir = Path.Combine(repoRoot, "data", "sources", "permissions", "github", "raw");
        var dstRawDir = Path.Combine(tempRepo, "data", "sources", "permissions", "github", "raw");
        Directory.CreateDirectory(dstRawDir);

        foreach (var file in Directory.GetFiles(srcRawDir))
        {
            File.Copy(file, Path.Combine(dstRawDir, Path.GetFileName(file)));
        }

        // The parse/merge stages resolve source URLs through the manifest.
        var dstDataDir = Path.Combine(tempRepo, "data", "sources");
        Directory.CreateDirectory(dstDataDir);
        File.Copy(
            Path.Combine(repoRoot, "data", "sources", "manifest.json"),
            Path.Combine(dstDataDir, "manifest.json"));

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
