using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class ExclusionMatcherTests
{
    [Test]
    public async Task IsFileFullyExcluded_FileLevelPattern_MatchesAbsolutePath()
    {
        var exclusions = new List<LintExclusion>
        {
            new(".github/workflows/legacy.yml", Rules: null, Jobs: null),
        };

        var isExcluded = ExclusionMatcher.IsFileFullyExcluded(
            exclusions,
            Path.GetFullPath(".github/workflows/legacy.yml"));

        await Assert.That(isExcluded).IsTrue();
    }

    [Test]
    public async Task IsFileFullyExcluded_JobScopedPattern_ReturnsFalse()
    {
        var exclusions = new List<LintExclusion>
        {
            new(".github/workflows/ci.yml", Rules: null, Jobs: ["build"]),
        };

        var isExcluded = ExclusionMatcher.IsFileFullyExcluded(
            exclusions,
            Path.GetFullPath(".github/workflows/ci.yml"));

        await Assert.That(isExcluded).IsFalse();
    }
}
