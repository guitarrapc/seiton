using Seiton.Output;

namespace Seiton.Tests;

public sealed class DecimalFormatTests
{
    [Test]
    [Arguments(0, 1)]
    [Arguments(1, 1)]
    [Arguments(9, 1)]
    [Arguments(10, 2)]
    [Arguments(99, 2)]
    [Arguments(100, 3)]
    [Arguments(999, 3)]
    [Arguments(1000, 4)]
    [Arguments(9999, 4)]
    [Arguments(12345, 5)]
    [Arguments(999_999, 6)]
    [Arguments(1_000_000, 7)]
    [Arguments(99_999_999, 8)]
    [Arguments(999_999_999, 9)]
    [Arguments(1_000_000_000, 10)]
    [Arguments(int.MaxValue, 10)]
    public async Task CountDigits_MatchesDecimalLength(int value, int expected)
    {
        await Assert.That(DecimalFormat.CountDigits(value)).IsEqualTo(expected);
    }
}
