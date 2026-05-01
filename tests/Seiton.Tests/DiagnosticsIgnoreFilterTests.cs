using Seiton.Commands;

namespace Seiton.Tests;

public sealed class DiagnosticsIgnoreFilterTests
{
    [Test]
    public async Task EmptyPattern_DoesNotSuppressAll()
    {
        string[] patterns = [""];
        var ignored = DiagnosticsIgnoreFilter.IsMessageIgnored(patterns, "some diagnostic message");

        await Assert.That(ignored).IsFalse();
    }

    [Test]
    public async Task WhitespacePattern_DoesNotSuppressAll()
    {
        string[] patterns = [" "];
        var ignored = DiagnosticsIgnoreFilter.IsMessageIgnored(patterns, "some diagnostic message");

        await Assert.That(ignored).IsFalse();
    }

    [Test]
    public async Task NormalPattern_StillMatches()
    {
        string[] patterns = ["unpinned"];
        var ignored = DiagnosticsIgnoreFilter.IsMessageIgnored(patterns, "action is unpinned");

        await Assert.That(ignored).IsTrue();
    }

    [Test]
    public async Task EmptyPatternAmongValid_OnlyValidMatches()
    {
        string[] patterns = ["", "pinned"];
        var ignoredUnrelated = DiagnosticsIgnoreFilter.IsMessageIgnored(patterns, "some other diagnostic");

        await Assert.That(ignoredUnrelated).IsFalse();
    }

    [Test]
    public async Task NoPatterns_NeverIgnored()
    {
        string[] patterns = [];
        var ignored = DiagnosticsIgnoreFilter.IsMessageIgnored(patterns, "anything");

        await Assert.That(ignored).IsFalse();
    }
}
