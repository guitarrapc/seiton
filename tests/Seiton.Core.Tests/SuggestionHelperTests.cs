using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class SuggestionHelperTests
{
    [Test]
    public async Task FormatExpectedOptions_Empty_ReturnsEmptyString()
    {
        var result = SuggestionHelper.FormatExpectedOptions([]);
        await Assert.That(result).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task FormatExpectedOptions_SingleOption_ReturnsQuoted()
    {
        var result = SuggestionHelper.FormatExpectedOptions(["run"]);
        await Assert.That(result).IsEqualTo("\"run\"");
    }

    [Test]
    public async Task FormatExpectedOptions_MultipleOptions_ReturnsCommaSeparatedQuoted()
    {
        var result = SuggestionHelper.FormatExpectedOptions(["branches", "paths", "tags"]);
        await Assert.That(result).IsEqualTo("\"branches\", \"paths\", \"tags\"");
    }

    [Test]
    public async Task FindClosest_CaseDifferentInput_SuggestsMatch()
    {
        // "BRANCHES" should match "branches" with case-insensitive distance 0
        var result = SuggestionHelper.FindClosest("BRANCHES", ["branches", "paths", "tags"]);
        await Assert.That(result).IsEqualTo("branches");
    }

    [Test]
    public async Task FindClosest_MixedCaseWithTypo_SuggestsMatch()
    {
        // "Branchs" should match "branches" with case-insensitive distance 1 (missing 'e')
        var result = SuggestionHelper.FindClosest("Branchs", ["branches", "paths", "tags"]);
        await Assert.That(result).IsEqualTo("branches");
    }
}
