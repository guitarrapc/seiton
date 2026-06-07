using Seiton.Cli;
using Seiton.Commands;
using Seiton.Output;

namespace Seiton.Tests;

public sealed class FeedbackMigrationCliTests
{
    private static string FixtureRoot =>
        Path.Combine(FindRepoRoot(), "tests", "Seiton.Core.Tests", "fixtures", "migration");

    private static string ConfigPath => Path.Combine(FixtureRoot, ".github", "seiton.yaml");

    [Test]
    public async Task ValidateConfig_MigratedFixture_Succeeds()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = ValidateCommand.Run(
            ConfigPath,
            verboseLevel: VerboseLevel.Summary,
            baseDirectory: FixtureRoot,
            output: stdout,
            error: stderr);

        await Assert.That(exitCode).IsEqualTo(ExitCode.Success);
        await Assert.That(stdout.ToString()).Contains("config valid:");
        await Assert.That(stderr.ToString()).Contains("verbose: job-id-check:");
    }

    [Test]
    [NotInParallel("FeedbackMigrationFixture")]
    public async Task Check_MigratedFixture_Verbose_ReportsDiscoverySkipAndSuppression()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Directory.SetCurrentDirectory(FixtureRoot);
#pragma warning disable TUnit0055
            Console.SetOut(stdout);
            Console.SetError(stderr);
#pragma warning restore TUnit0055

            var exitCode = CheckCommand.Run(
                [],
                config: ConfigPath,
                stdinFilename: "stdin.yml",
                ignore: [],
                minSeverity: null,
                format: OutputFormat.Text,
                oneline: false,
                color: ColorMode.Never,
                noColor: true,
                verboseLevel: VerboseLevel.Summary,
                includeActions: false);

            var err = stderr.ToString();
            await Assert.That(exitCode).IsEqualTo(ExitCode.LintIssuesFound);
            await Assert.That(err).Contains("skipped");
            await Assert.That(err).Contains("agentic workflow");
            await Assert.That(err).Contains("suppressed:");
            await Assert.That(err).DoesNotContain("unknown job-id");
            await Assert.That(err).Contains("1 excluded");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
#pragma warning disable TUnit0055
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
#pragma warning restore TUnit0055
        }
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "seiton.slnx")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
