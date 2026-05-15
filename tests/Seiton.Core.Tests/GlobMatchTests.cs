using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class GlobMatchTests
{
    [Test]
    public async Task GlobMatch_DoubleStarSlashMatchesRootLevelFile()
    {
        await Assert.That(ActionRefHelpers.GlobMatch("**/*.yml", "file.yml")).IsTrue();
    }

    [Test]
    public async Task GlobMatch_DoubleStarSlashMatchesNestedFile()
    {
        await Assert.That(ActionRefHelpers.GlobMatch("**/*.yml", ".github/workflows/file.yml")).IsTrue();
    }
}
