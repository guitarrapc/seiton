using Seiton.Update.Generators;
using Seiton.Update.Parsers;
using Seiton.Update.Services;

namespace Seiton.Update.Tests;

public sealed class UnpinnedToolsPipelineTests
{
    [Test]
    public async Task Parse_ReturnsExpectedActions()
    {
        var repoRoot = FindRepoRoot();
        var sourcePath = UnpinnedToolsSourcePathResolver.ResolvePrimary(repoRoot);

        var parser = new UnpinnedToolsSourceParser();
        var model = parser.Parse(sourcePath);

        await Assert.That(model.Actions.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(model.Actions[0].Owner).IsEqualTo("aquasecurity");
        await Assert.That(model.Actions[0].Repo).IsEqualTo("setup-trivy");
        await Assert.That(model.Actions[0].VersionInput).IsEqualTo("version");
    }

    [Test]
    public async Task Parse_NormalizesOwnerRepoToLowercase()
    {
        var repoRoot = FindRepoRoot();
        var sourcePath = UnpinnedToolsSourcePathResolver.ResolvePrimary(repoRoot);

        var parser = new UnpinnedToolsSourceParser();
        var model = parser.Parse(sourcePath);

        foreach (var action in model.Actions)
        {
            await Assert.That(action.Owner).IsEqualTo(action.Owner.ToLowerInvariant());
            await Assert.That(action.Repo).IsEqualTo(action.Repo.ToLowerInvariant());
        }
    }

    [Test]
    public async Task Sync_GeneratedFileIsUpToDate()
    {
        var repoRoot = FindRepoRoot();
        var syncService = new UnpinnedToolsSyncService();

        await Assert.That(syncService.IsUpToDate(repoRoot)).IsTrue();
    }

    [Test]
    public async Task Generate_ProducesValidCSharp()
    {
        var repoRoot = FindRepoRoot();
        var sourcePath = UnpinnedToolsSourcePathResolver.ResolvePrimary(repoRoot);

        var parser = new UnpinnedToolsSourceParser();
        var model = parser.Parse(sourcePath);

        var generator = new UnpinnedToolsCSharpGenerator();
        var code = generator.Generate(model);

        await Assert.That(code).Contains("TryGetKnownActionIndex");
        await Assert.That(code).Contains("GetVersionInputKey");
        await Assert.That(code).Contains("GetMissingVersionMessage");
        await Assert.That(code).Contains("GetLatestMessage");
        await Assert.That(code).Contains("GetDynamicMessage");
        await Assert.That(code).Contains("aquasecurity/setup-trivy");
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
