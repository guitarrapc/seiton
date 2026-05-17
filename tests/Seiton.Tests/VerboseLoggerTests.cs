using Seiton.Cli;

namespace Seiton.Tests;

public sealed class VerboseLoggerTests
{
    [Test]
    public async Task Log_CategoryAndMessage_WritesFormattedLine()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        logger.Log("config", ".github/seiton.yaml");

        await Assert.That(sw.ToString().TrimEnd()).IsEqualTo("verbose: config: .github/seiton.yaml");
    }

    [Test]
    public async Task Log_MessageOnly_WritesWithPrefix()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        logger.Log("checking file.yml...");

        await Assert.That(sw.ToString().TrimEnd()).IsEqualTo("verbose: checking file.yml...");
    }

    [Test]
    public async Task LogFile_WritesFilePathAsCategory()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        logger.LogFile(".github/workflows/ci.yml", "workflow, 1.2 ms");

        await Assert.That(sw.ToString().TrimEnd()).IsEqualTo("verbose: .github/workflows/ci.yml: workflow, 1.2 ms");
    }

    [Test]
    public async Task IsEnabled_WhenVerbose_ReturnsTrue()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        await Assert.That(logger.IsEnabled).IsTrue();
    }

    [Test]
    public async Task IsEnabled_WhenNotVerbose_ReturnsFalse()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: false, sw);

        await Assert.That(logger.IsEnabled).IsFalse();
    }

    [Test]
    public async Task Null_IsNotEnabled()
    {
        await Assert.That(VerboseLogger.Null.IsEnabled).IsFalse();
    }

    [Test]
    public async Task Null_Log_ProducesNoOutput()
    {
        // VerboseLogger.Null should not throw or produce output
        VerboseLogger.Null.Log("config", "test");
        VerboseLogger.Null.Log("test");
        VerboseLogger.Null.LogFile("file.yml", "test");

        // Null logger should not throw — verify it completed
        await Assert.That(VerboseLogger.Null.IsEnabled).IsFalse();
    }

    [Test]
    public async Task Create_VerboseFalse_ProducesNoOutput()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: false, sw);

        logger.Log("config", "test");
        logger.Log("test");
        logger.LogFile("file.yml", "test");

        await Assert.That(sw.ToString()).IsEqualTo("");
    }

    [Test]
    public async Task Log_MultipleCategories_EachOnSeparateLine()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(verbose: true, sw);

        logger.Log("config", ".github/seiton.yaml");
        logger.Log("discovery", "3 file(s) resolved");

        var lines = sw.ToString().TrimEnd().Split(Environment.NewLine);
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo("verbose: config: .github/seiton.yaml");
        await Assert.That(lines[1]).IsEqualTo("verbose: discovery: 3 file(s) resolved");
    }
}
