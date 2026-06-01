using Seiton.Cli;
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
                verboseLevel: VerboseLevel.Off,
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
    public async Task FixCheck_MinSeverityError_DoesNotShowSummaryForFilteredFixableWarnings()
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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: true,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(stderr.ToString().Contains("fixable", StringComparison.Ordinal)).IsFalse();
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
                verboseLevel: VerboseLevel.Off,
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
                    uses: actions/checkout@v4
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
                verboseLevel: VerboseLevel.Off,
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
            await Assert.That(errorOutput).Contains("1 warning remains in 1 file");

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
                verboseLevel: VerboseLevel.Summary,
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
                verboseLevel: VerboseLevel.Off,
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
                verboseLevel: VerboseLevel.Off,
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
                verboseLevel: VerboseLevel.Off,
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
                verboseLevel: VerboseLevel.Off,
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

    [Test]
    public async Task Fix_InvalidConfig_IgnoreActionsString_ReportsConfigError()
    {
        // The old ignore-actions format (bare string) is no longer valid.
        // The CLI must report a structured config error, not an unhandled exception.
        var configPath = CreateConfigFile(
            """
            rules:
              unpinned-uses:
                ignore-actions:
                  - guitarrapc/actions
            fix:
              defaults:
                job-timeout-minutes: 15
            """);
        var filePath = CreateWorkflowFile(
            """
            on: push
            jobs:
              test:
                runs-on: ubuntu-24.04
                steps:
                  - uses: actions/checkout@v4
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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            // Must exit with FatalError (not crash with stack trace)
            await Assert.That(exitCode).IsEqualTo(ExitCode.FatalError);
            // Error output must mention what's wrong with config
            await Assert.That(errorOutput).Contains("ignore-actions");
            // Must NOT contain a raw .NET stack trace
            await Assert.That(errorOutput).DoesNotContain("System.InvalidOperationException");
            await Assert.That(errorOutput).DoesNotContain("at Seiton.");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_AutoDiscoveredInvalidConfig_IgnoreActionsString_ReportsConfigError()
    {
        var repoDir = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        var githubDir = Path.Combine(repoDir, ".github");
        Directory.CreateDirectory(githubDir);
        var configPath = Path.Combine(githubDir, "seiton.yaml");
        File.WriteAllText(configPath,
            """
            rules:
              unpinned-uses:
                ignore-actions:
                  - guitarrapc/actions
            """);
        var filePath = Path.Combine(repoDir, "workflow.yml");
        File.WriteAllText(filePath,
            """
            on: push
            jobs:
              test:
                runs-on: ubuntu-24.04
                steps:
                  - uses: actions/checkout@v4
            """);

        var originalDirectory = Directory.GetCurrentDirectory();
        var originalConfig = Environment.GetEnvironmentVariable("SEITON_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("SEITON_CONFIG", null);
            Directory.SetCurrentDirectory(repoDir);
            using var sw = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await FixCommand.RunAsync(
                ["workflow.yml"],
                config: null,
                stdinFilename: "stdin.yml",
                ignore: [],
                minSeverity: null,
                format: OutputFormat.Text,
                oneline: true,
                color: ColorMode.Never,
                noColor: true,
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.FatalError);
            await Assert.That(stderr.ToString()).Contains("ignore-actions item must be a mapping with owner and optional refs");
            await Assert.That(stderr.ToString()).DoesNotContain("System.InvalidOperationException");
            await Assert.That(stderr.ToString()).DoesNotContain("at Seiton.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEITON_CONFIG", originalConfig);
            Directory.SetCurrentDirectory(originalDirectory);
            if (Directory.Exists(repoDir))
                Directory.Delete(repoDir, recursive: true);
        }
    }

    [Test]
    public async Task CreateFixApplicationErrorLines_NonVerbose_OmitsStackTrace()
    {
        var ex = new InvalidOperationException("overlapping or conflicting edits detected at offset 78");

        var lines = FixCommand.CreateFixApplicationErrorLines("workflow.yml", ex, verbose: false);

        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("error: fix failed for workflow.yml");
        await Assert.That(lines[0]).Contains("offset 78");
        await Assert.That(lines[1]).Contains("hint:");
    }

    [Test]
    public async Task CreateFixApplicationErrorLines_Verbose_IncludesDetailLine()
    {
        var ex = new InvalidOperationException("boom");

        var lines = FixCommand.CreateFixApplicationErrorLines("workflow.yml", ex, verbose: true);

        // Never-thrown exception has no StackTrace, falls back to ex.ToString() (single line)
        await Assert.That(lines.Length).IsGreaterThanOrEqualTo(3);
        await Assert.That(lines[2]).StartsWith("detail:");
    }

    [Test]
    public async Task CreateFixApplicationErrorLines_Verbose_MultiLineStackTrace_PrefixesEachLine()
    {
        // Create an exception with a multi-line stack trace via nested call
        Exception ex;
        try { ThrowFromNestedCall(); ex = null!; }
        catch (Exception e) { ex = e; }

        var lines = FixCommand.CreateFixApplicationErrorLines("workflow.yml", ex, verbose: true);

        // Stack trace from nested call has multiple frames; each must be prefixed with "detail:"
        await Assert.That(lines.Length).IsGreaterThanOrEqualTo(3);
        for (var i = 2; i < lines.Length; i++)
        {
            await Assert.That(lines[i]).StartsWith("detail:");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowFromNestedCall() => ThrowFromInnerCall();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowFromInnerCall() => throw new InvalidOperationException("boom");

    [Test]
    public async Task CreateFixApplicationErrorLines_UnexpectedException_UsesSameFriendlyFormat()
    {
        var ex = new IndexOutOfRangeException("selector bug");

        var lines = FixCommand.CreateFixApplicationErrorLines("workflow.yml", ex, verbose: false);

        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("error: fix failed for workflow.yml");
        await Assert.That(lines[0]).Contains("selector bug");
        await Assert.That(lines[1]).Contains("Please report this issue");
    }

    [Test]
    public async Task CreateFixApplicationErrorLines_MessageWithNewlines_NormalizesToSingleLine()
    {
        var ex = new InvalidOperationException("line one\r\nline two\nline three");

        var lines = FixCommand.CreateFixApplicationErrorLines("workflow.yml", ex, verbose: false);

        // The error: line must be a single logical line — no embedded newlines
        await Assert.That(lines[0]).DoesNotContain("\n");
        await Assert.That(lines[0]).DoesNotContain("\r");
        await Assert.That(lines[0]).Contains("line one");
        await Assert.That(lines[0]).Contains("line two");
        await Assert.That(lines[0]).Contains("line three");
    }

    // === Fix Summary Tests ===

    [Test]
    public async Task Fix_Summary_ShowsFixedCountAndRemaining()
    {
        // Workflow with fixable issue (if-expr-wrapper) and non-fixable issue (job-timeout-minutes-required)
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            // Must contain "Fixed" summary line
            await Assert.That(errorOutput).Contains("Fixed");
            // Must show remaining count
            await Assert.That(errorOutput).Contains("remaining");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Summary_NotShown_WhenNoFixesApplied()
    {
        // Workflow with no fixable issues
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
                enabled: false
              job-permissions-required:
                enabled: false
              if-expr-wrapper:
                enabled: false
              job-timeout-minutes-required:
                enabled: false
            """);
        var filePath = CreateWorkflowFile(
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-24.04
                timeout-minutes: 10
                permissions:
                  contents: read
                steps:
                  - run: echo ok
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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            // Must NOT contain "Fixed" summary line
            await Assert.That(errorOutput).DoesNotContain("Fixed");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Summary_MultipleFiles_ShowsPerFileDetail()
    {
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
                enabled: false
              job-permissions-required:
                enabled: false
            """);

        // Create two workflow files, both fixable
        var dir = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var filePath1 = Path.Combine(dir, "workflow1.yml");
        var filePath2 = Path.Combine(dir, "workflow2.yml");
        File.WriteAllText(filePath1, """
            on: push
            jobs:
              build:
                runs-on: ubuntu-24.04
                steps:
                  - if: github.event_name == 'push'
                    run: echo ok
            """);
        File.WriteAllText(filePath2, """
            on: push
            jobs:
              test:
                runs-on: ubuntu-24.04
                steps:
                  - if: github.ref == 'refs/heads/main'
                    run: echo test
            """);

        try
        {
            using var sw = new StringWriter();
            using var stderr = new StringWriter();

            await FixCommand.RunAsync(
                [filePath1, filePath2],
                config: configPath,
                stdinFilename: "stdin.yml",
                ignore: [],
                minSeverity: null,
                format: OutputFormat.Text,
                oneline: true,
                color: ColorMode.Never,
                noColor: true,
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            // Must contain per-file details in table format
            await Assert.That(errorOutput).Contains("| workflow1.yml");
            await Assert.That(errorOutput).Contains("| workflow2.yml");
            await Assert.That(errorOutput).Contains("| Fixed");
            // Must contain total summary
            await Assert.That(errorOutput).Contains("Fixed");
        }
        finally
        {
            DeleteContainingDirectory(filePath1);
            DeleteContainingDirectory(configPath);
        }
    }

    // === Fix Summary in Dry-Run / Check Mode Tests ===

    [Test]
    public async Task Fix_Summary_DryRun_ShowsSummary()
    {
        // Workflow with fixable issue (if-expr-wrapper)
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
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
                verboseLevel: VerboseLevel.Off,
                dryRun: true,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            // Must contain "Would fix" summary line in dry-run mode
            await Assert.That(errorOutput).Contains("Would fix");
            await Assert.That(errorOutput).Contains("remaining");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Summary_Check_ShowsSummary()
    {
        // Workflow with fixable issue (if-expr-wrapper)
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: true,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            // Must contain fixable summary line in check mode
            await Assert.That(errorOutput).Contains("fixable");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Summary_DryRun_NotShown_WhenNoFixesApplied()
    {
        // Workflow with no fixable issues
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
                enabled: false
              job-permissions-required:
                enabled: false
              if-expr-wrapper:
                enabled: false
              job-timeout-minutes-required:
                enabled: false
            """);
        var filePath = CreateWorkflowFile(
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-24.04
                timeout-minutes: 10
                permissions:
                  contents: read
                steps:
                  - run: echo ok
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
                verboseLevel: VerboseLevel.Off,
                dryRun: true,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            // Must NOT contain fix summary when nothing is fixable
            await Assert.That(errorOutput).DoesNotContain("Would fix");
            await Assert.That(errorOutput).DoesNotContain("fixable");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Summary_DryRun_IncludesUnfixedFilesWithRemaining()
    {
        // Two files: one with fixable issues, one with only non-fixable issues.
        // The summary should include BOTH files — the unfixed file as "would fix 0, remaining N".
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
                enabled: false
              job-permissions-required:
                enabled: false
            """);

        var dir = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // File 1: has fixable issue (if-expr-wrapper)
        var filePath1 = Path.Combine(dir, "fixable.yml");
        File.WriteAllText(filePath1, """
            on: push
            jobs:
              build:
                runs-on: ubuntu-24.04
                timeout-minutes: 10
                steps:
                  - if: github.event_name == 'push'
                    run: echo ok
            """);

        // File 2: has only non-fixable issue (job-timeout-minutes-required — no auto-fix)
        var filePath2 = Path.Combine(dir, "unfixable.yml");
        File.WriteAllText(filePath2, """
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

            await FixCommand.RunAsync(
                [filePath1, filePath2],
                config: configPath,
                stdinFilename: "stdin.yml",
                ignore: [],
                minSeverity: null,
                format: OutputFormat.Text,
                oneline: true,
                color: ColorMode.Never,
                noColor: true,
                verboseLevel: VerboseLevel.Off,
                dryRun: true,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            // Fix summary should show the fixable file
            await Assert.That(errorOutput).Contains("fixable.yml");
            // Fix summary should ALSO show the unfixable file with its remaining count
            await Assert.That(errorOutput).Contains("unfixable.yml");
            await Assert.That(errorOutput).Contains("remaining");
            // Total remaining should reflect the unfixed file's issues
            var totalLine = errorOutput.Split('\n').FirstOrDefault(l => l.StartsWith("Would fix"));
            await Assert.That(totalLine).IsNotNull();
            await Assert.That(totalLine!).Contains("in 2 files");
            // The remaining count in the total line should be > 0 (because unfixable.yml has issues)
            await Assert.That(totalLine!).DoesNotContain("(0 remaining)");
        }
        finally
        {
            DeleteContainingDirectory(filePath1);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_DryRun_JsonFormat_StdoutContainsOnlyValidJson()
    {
        // When --format json --dry-run is used, stdout must contain only valid JSON.
        // The unified diff must NOT appear on stdout (it should go to stderr or be suppressed).
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
                    uses: actions/checkout@v4
            """);

        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            await FixCommand.RunAsync(
                [filePath],
                config: configPath,
                stdinFilename: "stdin.yml",
                ignore: [],
                minSeverity: null,
                format: OutputFormat.Json,
                oneline: false,
                color: ColorMode.Never,
                noColor: true,
                verboseLevel: VerboseLevel.Off,
                dryRun: true,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: stdout,
                error: stderr);

            var output = stdout.ToString().Trim();

            // stdout must be valid JSON (either empty or a JSON array)
            if (output.Length > 0)
            {
                await Assert.That(output).StartsWith("[");
                await Assert.That(output).EndsWith("]");
                // Must not contain diff markers
                await Assert.That(output).DoesNotContain("---");
                await Assert.That(output).DoesNotContain("+++");
                await Assert.That(output).DoesNotContain("@@");
            }

            // The diff (if any) should appear on stderr, not stdout
            var errorOutput = stderr.ToString();
            // stderr may contain diff and/or summary — that's fine
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_DryRun_JsonFormat_DiffAppearsOnStderr()
    {
        // Verify that when --format json --dry-run produces a diff, it goes to stderr
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
                    uses: actions/checkout@v4
            """);

        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            await FixCommand.RunAsync(
                [filePath],
                config: configPath,
                stdinFilename: "stdin.yml",
                ignore: [],
                minSeverity: null,
                format: OutputFormat.Json,
                oneline: false,
                color: ColorMode.Never,
                noColor: true,
                verboseLevel: VerboseLevel.Off,
                dryRun: true,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: stdout,
                error: stderr);

            var errorOutput = stderr.ToString();

            // The diff should be in stderr (since format is json, stdout must be pure JSON)
            await Assert.That(errorOutput).Contains("---");
            await Assert.That(errorOutput).Contains("+++");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_DryRun_TextFormat_DiffStillAppearsOnStdout()
    {
        // When format is text, diff should still appear on stdout (existing behavior preserved)
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
                    uses: actions/checkout@v4
            """);

        try
        {
            using var stdout = new StringWriter();
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
                verboseLevel: VerboseLevel.Off,
                dryRun: true,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: stdout,
                error: stderr);

            var output = stdout.ToString();

            // For text format, diff must remain on stdout (existing behavior)
            await Assert.That(output).Contains("---");
            await Assert.That(output).Contains("+++");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    // === Fix Summary Order Tests (6a: fix summary before remain summary) ===

    [Test]
    public async Task Fix_Summary_Order_FixSummaryAppearsBeforeRemainSummary()
    {
        // Workflow with both fixable and non-fixable issues.
        // Verify fix summary (e.g. "Fixed N issues") appears BEFORE the remaining diagnostic summary.
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            var lines = errorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // Find the "Fixed" total line and the "remain" summary line.
            // The remain summary line uses "remain in" (not "(N remaining)" in the fix total).
            var fixedLineIndex = Array.FindIndex(lines, l => l.StartsWith("Fixed"));
            var remainLineIndex = Array.FindIndex(lines, l => (l.Contains("remain in") || l.Contains("remains in")) && !l.StartsWith("Fixed") && !l.TrimStart().StartsWith("workflow"));

            // Fix summary must appear BEFORE remaining summary
            await Assert.That(fixedLineIndex).IsGreaterThanOrEqualTo(0);
            await Assert.That(remainLineIndex).IsGreaterThan(fixedLineIndex);
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Summary_Order_RemainSummaryUsesRemainWording()
    {
        // Verify the remaining diagnostic summary uses "remain" wording in fix mode.
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            // The remaining summary must use "remain" wording (not just "in N files")
            await Assert.That(errorOutput).Contains("remain");
            // Should NOT use the old format "N errors, M warnings in N files" without "remain"
            // (The fix summary per-file lines use "remaining" which is fine)
            var lines = errorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var diagnosticSummaryLine = lines.FirstOrDefault(l =>
                (l.Contains("error") || l.Contains("warning")) && l.Contains("in") && l.Contains("file") && !l.Contains(":"));
            if (diagnosticSummaryLine is not null)
            {
                await Assert.That(diagnosticSummaryLine).Contains("remain");
            }
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Summary_Order_DryRun_FixSummaryAppearsBeforeRemainSummary()
    {
        // Same test for dry-run mode
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
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
                verboseLevel: VerboseLevel.Off,
                dryRun: true,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            var lines = errorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // Find the "Would fix" total line and the "remain" line
            var fixedLineIndex = Array.FindIndex(lines, l => l.StartsWith("Would fix"));
            var remainLineIndex = Array.FindIndex(lines, l =>
                (l.Contains("error") || l.Contains("warning") || l.Contains("0 issues")) && l.Contains("remain"));

            await Assert.That(fixedLineIndex).IsGreaterThanOrEqualTo(0);
            await Assert.That(remainLineIndex).IsGreaterThan(fixedLineIndex);
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Summary_Order_TotalLineAppearsBeforePerFileDetails()
    {
        // Verify the fix summary total line ("Fixed N issues in M files")
        // appears BEFORE per-file detail lines ("  file.yaml: fixed N, remaining M")
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            var lines = errorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // Total line: "Fixed N issues in M files"
            var totalLineIndex = Array.FindIndex(lines, l => l.StartsWith("Fixed"));
            // Per-file detail: table row starting with "| " and containing the file name
            var perFileIndex = Array.FindIndex(lines, l => l.Contains("| workflow.yml") || (l.Contains("workflow") && l.TrimStart().StartsWith('|')));

            await Assert.That(totalLineIndex).IsGreaterThanOrEqualTo(0);
            await Assert.That(perFileIndex).IsGreaterThan(totalLineIndex);
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    // === Fix Summary Relationship Tests (6c: before/after/fixed relationship) ===

    [Test]
    public async Task Fix_Summary_ShowsFoundCount_InTotalLine()
    {
        // When fixes are applied, the total line should show "Fixed X of Y issues"
        // where Y = X + remaining, making the relationship explicit.
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            // Total line must contain "of" to show relationship (e.g., "Fixed 1 of 2 issues")
            var totalLine = errorOutput.Split('\n').FirstOrDefault(l => l.StartsWith("Fixed"));
            await Assert.That(totalLine).IsNotNull();
            await Assert.That(totalLine!).Contains(" of ");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Summary_FoundCount_EqualsFixedPlusRemaining()
    {
        // The "of N" in the total line should equal fixed + remaining.
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            var totalLine = errorOutput.Split('\n').FirstOrDefault(l => l.StartsWith("Fixed"));
            await Assert.That(totalLine).IsNotNull();

            // Parse "Fixed X of Y issues in Z files (W remaining)"
            var match = System.Text.RegularExpressions.Regex.Match(totalLine!, @"Fixed (\d+) of (\d+) issues? in \d+ files? \((\d+) remaining\)");
            await Assert.That(match.Success).IsTrue();

            var fixedCount = int.Parse(match.Groups[1].Value);
            var foundCount = int.Parse(match.Groups[2].Value);
            var remainingCount = int.Parse(match.Groups[3].Value);
            await Assert.That(foundCount).IsEqualTo(fixedCount + remainingCount);
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Summary_DryRun_ShowsFoundCount()
    {
        // Dry-run mode should also show "Would fix X of Y issues"
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
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
                verboseLevel: VerboseLevel.Off,
                dryRun: true,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            var totalLine = errorOutput.Split('\n').FirstOrDefault(l => l.StartsWith("Would fix"));
            await Assert.That(totalLine).IsNotNull();
            await Assert.That(totalLine!).Contains(" of ");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Summary_Check_ShowsFoundCount()
    {
        // Check mode should also show "X of Y issues fixable"
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: true,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            var errorOutput = stderr.ToString();
            // Check mode: "X of Y issues fixable in Z files (W remaining)"
            await Assert.That(errorOutput).Contains(" of ");
            await Assert.That(errorOutput).Contains("fixable");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_ShowDiff_PrintsUnifiedDiffWhenApplying()
    {
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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: false,
                showDiff: true,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);

            var output = sw.ToString();
            await Assert.That(output).Contains("permissions:");
            await Assert.That(output).Contains("timeout-minutes: 15");

            var fixedContent = File.ReadAllText(filePath);
            await Assert.That(fixedContent).Contains("permissions:");
            await Assert.That(fixedContent).Contains("timeout-minutes: 15");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_ShowDiffAndDryRun_DryRunTakesPrecedence()
    {
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
                enabled: false
            fix:
              defaults:
                job-timeout-minutes: 15
            """);
        var originalYaml = """
            on:
              pull_request:
                branches: [main]
            jobs:
              test:
                runs-on: ubuntu-24.04
                steps:
                  - run: echo "hello"
            """;
        var filePath = CreateWorkflowFile(originalYaml);

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
                verboseLevel: VerboseLevel.Off,
                dryRun: true,
                check: false,
                showDiff: true,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(sw.ToString()).Contains("timeout-minutes: 15");
            await Assert.That(File.ReadAllText(filePath)).IsEqualTo(originalYaml);
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Verbose_NoFixableIssues_EmitsProcessedZeroModified()
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
                permissions:
                  contents: read
                timeout-minutes: 15
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
                verboseLevel: VerboseLevel.Summary,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(stderr.ToString()).Contains("verbose: total: 1 file(s) processed, 0 modified");
            await Assert.That(stderr.ToString().Contains("Fixed ", StringComparison.Ordinal)).IsFalse();
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Verbose_AppliesLocalFix_EmitsOneModified()
    {
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
                enabled: true
                fix-mapping:
                  ubuntu-latest: "ubuntu-24.04"
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
                permissions:
                  contents: read
                timeout-minutes: 15
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
                verboseLevel: VerboseLevel.Summary,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(stderr.ToString()).Contains("verbose: total: 1 file(s) processed, 1 modified");
            await Assert.That(stderr.ToString()).Contains("Fixed ");
            await Assert.That(File.ReadAllText(filePath)).Contains("ubuntu-24.04");
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_Verbose_DryRun_AppliesLocalFix_EmitsWouldBeModifiedTotal()
    {
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
                enabled: true
                fix-mapping:
                  ubuntu-latest: "ubuntu-24.04"
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
                permissions:
                  contents: read
                timeout-minutes: 15
                steps:
                  - run: echo ok
            """);
        var originalContent = File.ReadAllText(filePath);

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
                verboseLevel: VerboseLevel.Summary,
                dryRun: true,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(stderr.ToString()).Contains("verbose: total: 1 file(s) processed, 1 would be modified");
            await Assert.That(File.ReadAllText(filePath)).IsEqualTo(originalContent);
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_UnchangedContent_DoesNotRewriteFileTimestamp()
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
                permissions:
                  contents: read
                timeout-minutes: 15
                steps:
                  - run: echo ok
            """);

        try
        {
            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-5));
            var lastWriteUtcBefore = File.GetLastWriteTimeUtc(filePath);

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
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(File.GetLastWriteTimeUtc(filePath)).IsEqualTo(lastWriteUtcBefore);
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Fix_DryRun_NoFixableIssues_NoModificationHint()
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
                permissions:
                  contents: read
                timeout-minutes: 15
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
                verboseLevel: VerboseLevel.Off,
                dryRun: true,
                check: false,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false,
                output: sw,
                error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(stderr.ToString().Contains("would be modified", StringComparison.Ordinal)).IsFalse();
            await Assert.That(stderr.ToString().Contains("Would fix", StringComparison.Ordinal)).IsFalse();
        }
        finally
        {
            DeleteContainingDirectory(filePath);
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task WriteNoFilesModifiedHint_DryRunWithFixableRemaining_EmitsExpectedLine()
    {
        using var sw = new StringWriter();

        FixCommand.WriteNoFilesModifiedHint(sw, fixAttemptedFileCount: 1, fixableRemainingCount: 3, dryRun: true);

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("hint: no files would be modified (1 file processed; 3 fixable issues remain)");
    }

    [Test]
    public async Task WriteNoFilesModifiedHint_AppliedWithFixableRemaining_EmitsExpectedLine()
    {
        using var sw = new StringWriter();

        FixCommand.WriteNoFilesModifiedHint(sw, fixAttemptedFileCount: 1, fixableRemainingCount: 1, dryRun: false);

        await Assert.That(sw.ToString().TrimEnd())
            .IsEqualTo("hint: no files modified (1 file processed; 1 fixable issue remains)");
    }

    [Test]
    public async Task WriteNoFilesModifiedHint_NoFixAttempted_EmitsNothing()
    {
        using var sw = new StringWriter();

        FixCommand.WriteNoFilesModifiedHint(sw, fixAttemptedFileCount: 0, fixableRemainingCount: 0, dryRun: false);

        await Assert.That(sw.ToString()).IsEqualTo("");
    }

    [Test]
    public async Task WriteFixSummary_AllowsZeroFixedCount_WhenFixCountIsUnknown()
    {
        using var sw = new StringWriter();
        var fixedFiles = new List<(string FilePath, int FixedCount)> { ("workflow.yml", 0) };
        var remainingDiagnostics = new List<Seiton.Core.Parsing.Diagnostic>();

        FixCommand.WriteFixSummary(sw, fixedFiles, remainingDiagnostics, FixCommand.FixSummaryMode.Applied);

        var output = sw.ToString();
        await Assert.That(output).Contains("Fixed 0 of 0 issues in 1 file (0 remaining)");
        await Assert.That(output).Contains("| workflow.yml");
        await Assert.That(output).Contains("|     0 |");
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
