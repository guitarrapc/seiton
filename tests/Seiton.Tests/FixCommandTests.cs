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

    [Test]
    public async Task DryRun_EmitsBlankLineBetweenDiffAndDiagnostics()
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
            using var sw = new StringWriter();
            using var stderr = new StringWriter();

            await FixCommand.RunAsync(
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
                dryRun: true,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var output = sw.ToString();
            var errorOutput = stderr.ToString();

            // The output must contain a diff (starts with ---) and a diagnostic (warning [...])
            await Assert.That(output).Contains("---");
            await Assert.That(output).Contains("warning [");
            await Assert.That(errorOutput).Contains("1 warning in 1 file");

            // There must be a blank line (two consecutive newlines) between the diff block and the diagnostic
            // The diff ends with a context line, and then a blank line should appear before the diagnostic.
            var lines = output.Split('\n');
            var lastDiffLineIndex = -1;
            var firstDiagLineIndex = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimEnd('\r');
                if (trimmed.StartsWith(' ') || trimmed.StartsWith('+') || trimmed.StartsWith('-') || trimmed.StartsWith("@@"))
                    lastDiffLineIndex = i;
                // Diagnostics start with a file path containing ": warning [" or ": error ["
                if (trimmed.Contains(": warning [") || trimmed.Contains(": error ["))
                {
                    firstDiagLineIndex = i;
                    break;
                }
            }

            await Assert.That(lastDiffLineIndex).IsGreaterThan(-1);
            await Assert.That(firstDiagLineIndex).IsGreaterThan(lastDiffLineIndex);

            // Verify there's at least one blank line between the last diff line and the first diagnostic
            var hasBlankLine = false;
            for (var i = lastDiffLineIndex + 1; i < firstDiagLineIndex; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    hasBlankLine = true;
                    break;
                }
            }

            await Assert.That(hasBlankLine).IsTrue();
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
