using Seiton.Commands;

namespace Seiton.Tests;

public sealed class ValidateCommandTests
{
    [Test]
    public async Task Run_InvalidConfig_IgnoreActionsString_ReportsParseError()
    {
        var configPath = CreateConfigFile(
            """
            rules:
              unpinned-uses:
                ignore-actions:
                  - guitarrapc/actions
            """);

        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = ValidateCommand.Run(configPath, stdout, stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.LintIssuesFound);
            await Assert.That(stdout.ToString()).DoesNotContain("config valid:");
            await Assert.That(stderr.ToString()).Contains("ignore-actions item must be a mapping with owner and optional refs");
        }
        finally
        {
            DeleteContainingDirectory(configPath);
        }
    }

    private static string CreateConfigFile(string yaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "seiton.yml");
        File.WriteAllText(filePath, yaml);
        return filePath;
    }

    private static void DeleteContainingDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
