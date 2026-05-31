using Seiton.Cli;
using Seiton.Commands;

namespace Seiton.Tests;

public sealed class VerboseLevelSplitTests
{
    [Test]
    public async Task VerboseLogger_SummaryLevel_DoesNotLogFileProgress()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(VerboseLevel.Summary, sw);

        await Assert.That(logger.IsEnabled).IsTrue();
        await Assert.That(logger.LogFileProgress).IsFalse();

        logger.Log("config", "test");
        await Assert.That(sw.ToString()).Contains("verbose: config:");
    }

    [Test]
    public async Task VerboseLogger_FilesLevel_LogsFileProgress()
    {
        using var sw = new StringWriter();
        var logger = VerboseLogger.Create(VerboseLevel.Files, sw);

        await Assert.That(logger.LogFileProgress).IsTrue();

        logger.Log("checking file.yml...");
        await Assert.That(sw.ToString()).Contains("verbose: checking file.yml...");
    }
}
