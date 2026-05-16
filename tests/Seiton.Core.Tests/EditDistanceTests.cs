using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class EditDistanceTests
{
    // === Basic cases ===

    [Test]
    public async Task ComputeIgnoreCase_BothEmpty_ReturnsZero()
    {
        var result = EditDistance.ComputeIgnoreCase("", "");
        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeIgnoreCase_LeftEmpty_ReturnsRightLength()
    {
        var result = EditDistance.ComputeIgnoreCase("", "hello");
        await Assert.That(result).IsEqualTo(5);
    }

    [Test]
    public async Task ComputeIgnoreCase_RightEmpty_ReturnsLeftLength()
    {
        var result = EditDistance.ComputeIgnoreCase("hello", "");
        await Assert.That(result).IsEqualTo(5);
    }

    [Test]
    public async Task ComputeIgnoreCase_Identical_ReturnsZero()
    {
        var result = EditDistance.ComputeIgnoreCase("node-version", "node-version");
        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeIgnoreCase_CaseDifferent_ReturnsZero()
    {
        var result = EditDistance.ComputeIgnoreCase("Node-Version", "node-version");
        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeIgnoreCase_ShortNonAsciiExactMatch_ReturnsZero()
    {
        var result = EditDistance.ComputeIgnoreCase("caf\u00E9", "caf\u00E9");
        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeIgnoreCase_ShortNonAsciiCaseDifferent_ReturnsZero()
    {
        var result = EditDistance.ComputeIgnoreCase("\u00C5ngstr\u00F6m", "\u00E5NGSTR\u00D6M");
        await Assert.That(result).IsEqualTo(0);
    }

    // === Known distances (real GitHub Actions typos) ===

    [Test]
    public async Task ComputeIgnoreCase_TokenTransposition_ReturnsTwo()
    {
        // "tokne" → "token" (transposition = 2 edits in Levenshtein)
        var result = EditDistance.ComputeIgnoreCase("tokne", "token");
        await Assert.That(result).IsEqualTo(2);
    }

    [Test]
    public async Task ComputeIgnoreCase_NodeVersionUnderscore_ReturnsOne()
    {
        // "node_version" → "node-version" (substitution)
        var result = EditDistance.ComputeIgnoreCase("node_version", "node-version");
        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task ComputeIgnoreCase_RegistryUrl_ReturnsOne()
    {
        // "registryUrl" → "registry-url" (missing hyphen = 1 insertion)
        var result = EditDistance.ComputeIgnoreCase("registryurl", "registry-url");
        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task ComputeIgnoreCase_CacheDependencyPathTypo_ReturnsOne()
    {
        // Extra 'x' at end
        var result = EditDistance.ComputeIgnoreCase("cache-dependency-pathx", "cache-dependency-path");
        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task ComputeIgnoreCase_BranchesTypo_ReturnsOne()
    {
        // "branchs" → "branches" (missing 'e')
        var result = EditDistance.ComputeIgnoreCase("branchs", "branches");
        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task ComputeIgnoreCase_CompletelyDifferent_ReturnsHighDistance()
    {
        var result = EditDistance.ComputeIgnoreCase("xyz", "abc");
        await Assert.That(result).IsEqualTo(3);
    }

    // === maxDistance overload tests ===

    [Test]
    public async Task ComputeIgnoreCase_WithMaxDistance_ReturnsExactWhenWithinThreshold()
    {
        // Distance is 1, maxDistance is 2 → should return 1
        var result = EditDistance.ComputeIgnoreCase("branchs", "branches", maxDistance: 2);
        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task ComputeIgnoreCase_WithMaxDistance_ReturnsMaxPlusOneWhenExceeded()
    {
        // "xyz" vs "abcdef" → distance is 6, maxDistance is 2 → should return 3
        var result = EditDistance.ComputeIgnoreCase("xyz", "abcdef", maxDistance: 2);
        await Assert.That(result).IsEqualTo(3);
    }

    [Test]
    public async Task ComputeIgnoreCase_WithMaxDistance_LengthDifferenceExceedsThreshold()
    {
        // |5 - 1| = 4 > maxDistance 2 → early return maxDistance + 1
        var result = EditDistance.ComputeIgnoreCase("hello", "x", maxDistance: 2);
        await Assert.That(result).IsEqualTo(3);
    }

    [Test]
    public async Task ComputeIgnoreCase_WithMaxDistance_ExactAtThreshold()
    {
        // Distance exactly equals maxDistance → should return exact distance
        var result = EditDistance.ComputeIgnoreCase("abc", "xyz", maxDistance: 3);
        await Assert.That(result).IsEqualTo(3);
    }

    [Test]
    public async Task ComputeIgnoreCase_WithMaxDistance_ZeroThreshold_Identical()
    {
        var result = EditDistance.ComputeIgnoreCase("token", "token", maxDistance: 0);
        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeIgnoreCase_WithMaxDistance_ZeroThreshold_Different()
    {
        var result = EditDistance.ComputeIgnoreCase("token", "tokne", maxDistance: 0);
        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task ComputeIgnoreCase_WithMaxDistance_CaseInsensitive()
    {
        // Case-insensitive distance is 0, maxDistance is 1
        var result = EditDistance.ComputeIgnoreCase("TOKEN", "token", maxDistance: 1);
        await Assert.That(result).IsEqualTo(0);
    }

    // === Symmetry ===

    [Test]
    public async Task ComputeIgnoreCase_IsSymmetric()
    {
        var ab = EditDistance.ComputeIgnoreCase("node-version", "node_version");
        var ba = EditDistance.ComputeIgnoreCase("node_version", "node-version");
        await Assert.That(ab).IsEqualTo(ba);
    }

    [Test]
    public async Task ComputeIgnoreCase_WithMaxDistance_IsSymmetric()
    {
        var ab = EditDistance.ComputeIgnoreCase("node-version", "node_version", maxDistance: 2);
        var ba = EditDistance.ComputeIgnoreCase("node_version", "node-version", maxDistance: 2);
        await Assert.That(ab).IsEqualTo(ba);
    }

    // === Consistency between overloads ===

    [Test]
    public async Task ComputeIgnoreCase_WithLargeMaxDistance_MatchesUnbounded()
    {
        var unbounded = EditDistance.ComputeIgnoreCase("environment-url", "environment_url");
        var bounded = EditDistance.ComputeIgnoreCase("environment-url", "environment_url", maxDistance: 100);
        await Assert.That(bounded).IsEqualTo(unbounded);
    }

    [Test]
    public async Task ComputeIgnoreCase_LengthBoundary64And65_MatchesExpectedDistance()
    {
        var left64 = new string('a', 64);
        var right64 = new string('a', 63) + "b";
        var left65 = new string('a', 65);
        var right65 = new string('a', 64) + "b";

        await Assert.That(EditDistance.ComputeIgnoreCase(left64, right64)).IsEqualTo(1);
        await Assert.That(EditDistance.ComputeIgnoreCase(left65, right65)).IsEqualTo(1);
    }

    [Test]
    public async Task ComputeIgnoreCase_LongInputsBeyondStackallocThreshold_MatchesBetweenOverloads()
    {
        var left = new string('a', 129);
        var right = new string('a', 128) + "b";

        var unbounded = EditDistance.ComputeIgnoreCase(left, right);
        var bounded = EditDistance.ComputeIgnoreCase(left, right, maxDistance: 2);

        await Assert.That(unbounded).IsEqualTo(1);
        await Assert.That(bounded).IsEqualTo(1);
    }

    // === Real-world scenario: "did you mean?" with multiple candidates ===

    [Test]
    public async Task ComputeIgnoreCase_FindClosestCandidate_WorksCorrectly()
    {
        var input = "node_version";
        string[] candidates = ["node-version", "registry-url", "token", "cache-dependency-path"];
        var maxDistance = 3; // threshold for input.Length = 12

        var bestCandidate = (string?)null;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < candidates.Length; i++)
        {
            var d = EditDistance.ComputeIgnoreCase(input, candidates[i], maxDistance);
            if (d < bestDistance)
            {
                bestDistance = d;
                bestCandidate = candidates[i];
            }
        }

        await Assert.That(bestCandidate).IsEqualTo("node-version");
        await Assert.That(bestDistance).IsEqualTo(1);
    }

    // === Edge cases ===

    [Test]
    public async Task ComputeIgnoreCase_SingleChar_Same()
    {
        var result = EditDistance.ComputeIgnoreCase("a", "a");
        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeIgnoreCase_SingleChar_Different()
    {
        var result = EditDistance.ComputeIgnoreCase("a", "b");
        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task ComputeIgnoreCase_SingleCharVsEmpty()
    {
        var result = EditDistance.ComputeIgnoreCase("a", "");
        await Assert.That(result).IsEqualTo(1);
    }

    // === Banded DP correctness: verify maxDistance overload matches unbounded for various distances ===

    [Test]
    [Arguments("kitten", "sitting", 3)]      // distance = 3
    [Arguments("saturday", "sunday", 3)]      // distance = 3
    [Arguments("flaw", "lawn", 2)]            // distance = 2
    [Arguments("gumbo", "gambol", 2)]         // distance = 2
    [Arguments("book", "back", 2)]            // distance = 2
    public async Task ComputeIgnoreCase_WithMaxDistance_MatchesUnbounded_WhenWithinThreshold(string a, string b, int maxDist)
    {
        var unbounded = EditDistance.ComputeIgnoreCase(a, b);
        var bounded = EditDistance.ComputeIgnoreCase(a, b, maxDistance: maxDist);
        await Assert.That(bounded).IsEqualTo(unbounded);
    }

    [Test]
    [Arguments("abc", "xyz", 1)]              // distance = 3, maxDistance = 1 → returns 2
    [Arguments("hello", "world", 2)]          // distance = 4, maxDistance = 2 → returns 3
    [Arguments("kitten", "sitting", 2)]       // distance = 3, maxDistance = 2 → returns 3
    public async Task ComputeIgnoreCase_WithMaxDistance_ReturnsMaxPlusOne_WhenExceeded(string a, string b, int maxDist)
    {
        var result = EditDistance.ComputeIgnoreCase(a, b, maxDistance: maxDist);
        await Assert.That(result).IsEqualTo(maxDist + 1);
    }

    // === Negative maxDistance throws ===

    [Test]
    public async Task ComputeIgnoreCase_WithNegativeMaxDistance_ThrowsArgumentOutOfRange()
    {
        await Assert.That(() => EditDistance.ComputeIgnoreCase("abc", "xyz", maxDistance: -1))
            .Throws<ArgumentOutOfRangeException>();
    }
}
