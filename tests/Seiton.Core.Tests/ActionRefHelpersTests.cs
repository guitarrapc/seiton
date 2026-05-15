using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class ActionRefHelpersTests
{
    [Test]
    public async Task ResolveLocalReferenceBaseDirectory_SlashNormalizedGithubPath_UsesRepositoryRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");

        var baseDirectory = ActionRefHelpers.ResolveLocalReferenceBaseDirectory(workflowPath, "./.github/actions/sample");

        await Assert.That(baseDirectory).IsEqualTo(ActionRefHelpers.NormalizePath(repositoryRoot));
        await Assert.That(baseDirectory).DoesNotContain("\\");
    }

    [Test]
    public async Task NormalizeFullPath_ReturnsSlashNormalizedAbsolutePath()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var normalized = ActionRefHelpers.NormalizeFullPath(ActionRefHelpers.NormalizePath(repositoryRoot), "./.github/workflows/reusable.yml");
        var expected = ActionRefHelpers.NormalizePath(Path.GetFullPath(Path.Combine(repositoryRoot, ".github", "workflows", "reusable.yml")));

        await Assert.That(normalized).IsEqualTo(expected);
        await Assert.That(normalized).IsNotNull();
        await Assert.That(normalized!).DoesNotContain("\\");
    }
}
