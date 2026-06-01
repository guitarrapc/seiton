using Seiton.Cli;

namespace Seiton.Tests;

public sealed class CliFormatArgsTests
{
    [Test]
    public async Task WasFormatSpecified_NoFormatFlag_ReturnsFalse()
    {
        await Assert.That(CliFormatArgs.WasFormatSpecified(["check", "workflow.yml"])).IsFalse();
    }

    [Test]
    public async Task WasFormatSpecified_LongFormatFlag_ReturnsTrue()
    {
        await Assert.That(CliFormatArgs.WasFormatSpecified(["check", "--format", "text", "workflow.yml"])).IsTrue();
    }

    [Test]
    public async Task WasFormatSpecified_ShortFormatFlag_ReturnsTrue()
    {
        await Assert.That(CliFormatArgs.WasFormatSpecified(["-f", "json"])).IsTrue();
    }

    [Test]
    public async Task WasFormatSpecified_EqualsSyntax_ReturnsTrue()
    {
        await Assert.That(CliFormatArgs.WasFormatSpecified(["--format=github-actions"])).IsTrue();
    }

    [Test]
    public async Task WasFormatSpecified_AfterDoubleDash_ReturnsFalse()
    {
        await Assert.That(CliFormatArgs.WasFormatSpecified(["check", "--", "--format", "text"])).IsFalse();
    }
}
