using Seiton.Commands;
using Seiton.Output;

namespace Seiton.Tests;

public sealed class FixCommandTests
{
    [Test]
    public async Task FixCheck_MinSeverityError_IgnoresFixableWarningsForExitCode()
    {
                var configPath = CreateConfigFile(
                        """
                        rules:
                            runner-no-latest:
                                enabled: false
                            job-timeout-minutes-required:
                                enabled: false
                            job-permissions-required:
                                enabled: false
                        """);
        var filePath = CreateWorkflowFile(
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - if: github.event_name == 'push'
                    run: echo ok
            """);

        try
        {
            var exitCode = await FixCommand.RunAsync(
                [filePath],
                config: configPath,
                stdinFilename: "stdin.yml",
                ignore: [],
                minSeverity: "error",
                format: OutputFormat.Text,
                oneline: true,
                color: ColorMode.Never,
                noColor: true,
                verbose: false,
                dryRun: false,
                check: true,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(File.ReadAllText(filePath)).Contains("if: github.event_name == 'push'");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task FixCheck_WithoutMinSeverity_FailsForFixableWarnings()
    {
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
                enabled: false
              job-timeout-minutes-required:
                enabled: false
              job-permissions-required:
                enabled: false
            """);
        var filePath = CreateWorkflowFile(
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - if: github.event_name == 'push'
                    run: echo ok
            """);

        try
        {
            var exitCode = await FixCommand.RunAsync(
                [filePath],
                config: configPath,
                stdinFilename: "stdin.yml",
                ignore: [],
                minSeverity: null,
                format: OutputFormat.Text,
                oneline: true,
                color: ColorMode.Never,
                noColor: true,
                verbose: false,
                dryRun: false,
                check: true,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false);

            await Assert.That(exitCode).IsEqualTo(ExitCode.LintIssuesFound);
            await Assert.That(File.ReadAllText(filePath)).Contains("if: github.event_name == 'push'");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    private static string CreateWorkflowFile(string yaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "workflow.yml");
        File.WriteAllText(filePath, yaml);
        return filePath;
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
