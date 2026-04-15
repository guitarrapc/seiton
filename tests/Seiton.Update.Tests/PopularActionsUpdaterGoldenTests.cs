using Seiton.Update.Generators;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Tests;

public sealed class PopularActionsUpdaterGoldenTests
{
    [Test]
    public async Task Generate_FromGitHubPrimarySource_MatchesPopularActionsGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        var sourcePath = Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "popular_actions.json");
        var goldenPath = Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "PopularActions.g.cs");

        var parser = new GitHubPopularActionsSourceParser();
        var generator = new PopularActionsCSharpGenerator();

        var actions = parser.Parse(sourcePath);
        var actual = generator.Generate(actions);
        var expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");

        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task Sync_WhenOutputAlreadyCurrent_IsIdempotent()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoCopy(repoRoot);

        try
        {
            var sync = new PopularActionsSyncService();

            var first = sync.Sync(tempRepo);
            var second = sync.Sync(tempRepo);

            await Assert.That(first).IsFalse();
            await Assert.That(second).IsFalse();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Test]
    public async Task Verify_WhenGeneratedFileIsStale_ReturnsFalseThenSyncRepairs()
    {
        var repoRoot = FindRepoRoot();
        var tempRepo = CreateTempRepoCopy(repoRoot);

        try
        {
            var outputPath = Path.Combine(tempRepo, "src", "Seiton.Core", "Generated", "PopularActions.g.cs");
            File.AppendAllText(outputPath, "// stale\n");

            var sync = new PopularActionsSyncService();
            var stale = sync.IsUpToDate(tempRepo);
            var changed = sync.Sync(tempRepo);
            var fixedNow = sync.IsUpToDate(tempRepo);

            await Assert.That(stale).IsFalse();
            await Assert.That(changed).IsTrue();
            await Assert.That(fixedNow).IsTrue();
        }
        finally
        {
            Directory.Delete(tempRepo, recursive: true);
        }
    }

    static string CreateTempRepoCopy(string repoRoot)
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "seiton-update-tests-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempRepo);
        Directory.CreateDirectory(Path.Combine(tempRepo, "data", "sources", "popular-actions", "github"));
        Directory.CreateDirectory(Path.Combine(tempRepo, "src", "Seiton.Core", "Generated"));

        File.Copy(
            Path.Combine(repoRoot, "data", "sources", "popular-actions", "github", "popular_actions.json"),
            Path.Combine(tempRepo, "data", "sources", "popular-actions", "github", "popular_actions.json"),
            overwrite: true);

        File.Copy(
            Path.Combine(repoRoot, "src", "Seiton.Core", "Generated", "PopularActions.g.cs"),
            Path.Combine(tempRepo, "src", "Seiton.Core", "Generated", "PopularActions.g.cs"),
            overwrite: true);

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
