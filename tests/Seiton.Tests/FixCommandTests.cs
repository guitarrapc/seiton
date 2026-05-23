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

    [Test]
    public async Task Verbose_NoAppliedFixes_DoesNotLogZeroFixLine()
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
                runs-on: ubuntu-24.04
                steps:
                  - run: echo ok
            """);

        try
        {
            using var sw = new StringWriter();
            using var stderr = new StringWriter();

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
                verbose: true,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(stderr.ToString()).DoesNotContain("applied 0 fix(es)");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_OverlappingInserts_PermissionsAndTimeoutMinutes_DoesNotThrow()
    {
        // Regression test: job-permissions-required and job-timeout-minutes-required both
        // insert at the same byte offset (after runs-on:). Previously this caused
        // "overlapping or conflicting edits detected at offset ..." exception.
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
                enabled: false
            fix:
              defaults:
                job-timeout-minutes: 15
            """);
        var filePath = CreateWorkflowFile(
            """
            on:
              pull_request:
                branches: [main]
            jobs:
              test:
                runs-on: ubuntu-24.04
                steps:
                  - run: echo "hello"
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
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            var fixedContent = File.ReadAllText(filePath);
            // Both permissions: and timeout-minutes: must be inserted
            await Assert.That(fixedContent).Contains("permissions:");
            await Assert.That(fixedContent).Contains("timeout-minutes: 15");
            // They must appear before steps:
            var permIdx = fixedContent.IndexOf("permissions:");
            var timeoutIdx = fixedContent.IndexOf("timeout-minutes:");
            var stepsIdx = fixedContent.IndexOf("steps:");
            await Assert.That(permIdx).IsGreaterThan(-1);
            await Assert.That(timeoutIdx).IsGreaterThan(-1);
            await Assert.That(stepsIdx).IsGreaterThan(-1);
            await Assert.That(permIdx).IsLessThan(stepsIdx);
            await Assert.That(timeoutIdx).IsLessThan(stepsIdx);
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_OverlappingInserts_DryRun_DoesNotThrow()
    {
        // dry-run should also not throw for overlapping fixes
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
                enabled: false
            fix:
              defaults:
                job-timeout-minutes: 15
            """);
        var filePath = CreateWorkflowFile(
            """
            on:
              pull_request:
                branches: [main]
            jobs:
              test:
                runs-on: ubuntu-24.04
                steps:
                  - run: echo "hello"
            """);

        try
        {
            using var sw = new StringWriter();
            using var stderr = new StringWriter();

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
                dryRun: true,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var output = sw.ToString();
            // dry-run should produce a diff containing both fixes
            await Assert.That(output).Contains("permissions:");
            await Assert.That(output).Contains("timeout-minutes: 15");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_MultipleJobs_EachWithOverlappingInserts_FixesAll()
    {
        // Multiple jobs each missing permissions and timeout-minutes
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
                enabled: false
            fix:
              defaults:
                job-timeout-minutes: 30
            """);
        var filePath = CreateWorkflowFile(
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-24.04
                steps:
                  - run: echo build
              test:
                runs-on: ubuntu-24.04
                steps:
                  - run: echo test
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
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            var fixedContent = File.ReadAllText(filePath);
            // Both jobs should have permissions and timeout-minutes
            var lines = fixedContent.Split('\n');
            var permCount = lines.Count(l => l.TrimStart().StartsWith("permissions:"));
            var timeoutCount = lines.Count(l => l.TrimStart().StartsWith("timeout-minutes:"));
            await Assert.That(permCount).IsGreaterThanOrEqualTo(2);
            await Assert.That(timeoutCount).IsGreaterThanOrEqualTo(2);
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

        [Test]
        public async Task Fix_MultiEditDiagnostic_WithAnotherFix_DoesNotOverflowBatchSelection()
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
                        permissions: write-all
                        jobs:
                            "build job":
                                runs-on: ubuntu-24.04
                                steps:
                                    - run: echo build
                            consumer1:
                                runs-on: ubuntu-24.04
                                needs: ["build job"]
                                steps:
                                    - run: echo one
                            consumer2:
                                runs-on: ubuntu-24.04
                                needs: ["build job"]
                                steps:
                                    - run: echo two
                            consumer3:
                                runs-on: ubuntu-24.04
                                needs: ["build job"]
                                steps:
                                    - run: echo three
                            consumer4:
                                runs-on: ubuntu-24.04
                                needs: ["build job"]
                                steps:
                                    - run: echo four
                            consumer5:
                                runs-on: ubuntu-24.04
                                needs: ["build job"]
                                steps:
                                    - run: echo five
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
                                check: false,
                                enablePinNetwork: false,
                                enableImageNetwork: false,
                                includeActions: false);

                        await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

                        var fixedContent = File.ReadAllText(filePath);
                        await Assert.That(fixedContent).Contains("build-job:");
                        await Assert.That(fixedContent).DoesNotContain("\"build job\":");
                        await Assert.That(fixedContent).Contains("needs: [build-job]");
                        await Assert.That(fixedContent).Contains("permissions: {}");
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
