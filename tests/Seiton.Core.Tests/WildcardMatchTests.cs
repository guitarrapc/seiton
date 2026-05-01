using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

/// <summary>Tests for <see cref="ActionRefHelpers.WildcardMatch(ReadOnlySpan{char}, ReadOnlySpan{char})"/>.</summary>
public sealed class WildcardMatchTests
{
    [Test]
    public async Task ExactMatch_ReturnsTrue()
    {
        await Assert.That(ActionRefHelpers.WildcardMatch("hello", "hello")).IsTrue();
    }

    [Test]
    public async Task StarMatchesAny_ReturnsTrue()
    {
        await Assert.That(ActionRefHelpers.WildcardMatch("actions/checkout", "actions/*")).IsTrue();
    }

    [Test]
    public async Task QuestionMarkMatchesSingleChar()
    {
        await Assert.That(ActionRefHelpers.WildcardMatch("v4", "v?")).IsTrue();
        await Assert.That(ActionRefHelpers.WildcardMatch("v42", "v?")).IsFalse();
    }

    [Test]
    public async Task StarMatchesEmpty()
    {
        await Assert.That(ActionRefHelpers.WildcardMatch("actions/", "actions/*")).IsTrue();
    }

    [Test]
    public async Task StarMatchesAll()
    {
        await Assert.That(ActionRefHelpers.WildcardMatch("anything", "*")).IsTrue();
    }

    [Test]
    public async Task EmptyPatternMatchesEmptyText()
    {
        await Assert.That(ActionRefHelpers.WildcardMatch("", "")).IsTrue();
    }

    [Test]
    public async Task EmptyPatternDoesNotMatchNonEmptyText()
    {
        await Assert.That(ActionRefHelpers.WildcardMatch("text", "")).IsFalse();
    }

    [Test]
    public async Task NoMatch_ReturnsFalse()
    {
        await Assert.That(ActionRefHelpers.WildcardMatch("actions/checkout", "github/*")).IsFalse();
    }

    [Test]
    public async Task MultipleStars()
    {
        await Assert.That(ActionRefHelpers.WildcardMatch("slsa-framework/slsa-github-generator/.github/workflows/generator_generic_slsa3.yml", "slsa-framework/*")).IsTrue();
    }

    [Test]
    public async Task StarSpansSlashes()
    {
        await Assert.That(ActionRefHelpers.WildcardMatch("a/b/c", "a*c")).IsTrue();
    }
}
