using Seiton.Commands;
using Seiton.Cli;

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

            var exitCode = ValidateCommand.Run(configPath, output: stdout, error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.LintIssuesFound);
            await Assert.That(stdout.ToString()).DoesNotContain("config valid:");
            await Assert.That(stderr.ToString()).Contains("ignore-actions item must be a mapping with owner and optional refs");
        }
        finally
        {
            DeleteContainingDirectory(configPath);
        }
    }

    [Test]
    public async Task Run_JobScopedExclusion_UnknownJobId_ReportsOnConfigPath()
    {
        var dir = CreateRepoDirectory(
            workflowYaml: """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            configYaml: """
            exclusions:
              - file: .github/workflows/ci.yml
                jobs:
                  - missing-job
                rules:
                  - deny-inherit-secrets
            """);

        try
        {
            var configPath = Path.Combine(dir, ".github", "seiton.yaml");
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = ValidateCommand.Run(
                configPath,
                baseDirectory: dir,
                output: stdout,
                error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.LintIssuesFound);
            await Assert.That(stdout.ToString()).DoesNotContain("config valid:");
            await Assert.That(stderr.ToString()).Contains("unknown job-id 'missing-job'");
            await Assert.That(stderr.ToString()).Contains("/.github/seiton.yaml:1:1");
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Run_ValidConfig_Verbose_EmitsConfigPathAndSummaryStats()
    {
        var configPath = CreateConfigFile(
            """
            rules:
              runner-no-latest:
                enabled: false
            exclusions:
              - file: .github/workflows/_test-*.yaml
            """);

        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = ValidateCommand.Run(configPath, verboseLevel: VerboseLevel.Summary, output: stdout, error: stderr);

            await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
            await Assert.That(stdout.ToString()).Contains("config valid:");

            var verboseOutput = stderr.ToString();
            await Assert.That(verboseOutput).Contains("verbose: config:");
            await Assert.That(verboseOutput).Contains("verbose: parse:");
            await Assert.That(verboseOutput).Contains("verbose: rules:");
            await Assert.That(verboseOutput).Contains("verbose: exclusions:");
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

    private static string CreateRepoDirectory(string workflowYaml, string configYaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        var workflowsDir = Path.Combine(dir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);
        File.WriteAllText(Path.Combine(workflowsDir, "ci.yml"), workflowYaml);
        File.WriteAllText(Path.Combine(dir, ".github", "seiton.yaml"), configYaml);
        return dir;
    }

    private static void DeleteContainingDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static void DeleteDirectory(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
