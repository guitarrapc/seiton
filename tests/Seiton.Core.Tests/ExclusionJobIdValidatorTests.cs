using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class ExclusionJobIdValidatorTests
{
    [Test]
    public async Task Validate_UnknownJobIdInMatchingWorkflow_ReportsErrorOnConfigPath()
    {
        var dir = CreateRepo(
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
            var workflowPath = Path.Combine(dir, ".github", "workflows", "ci.yml");
            var validation = LintConfigLibrary.ValidateFile(configPath);

            var diags = ExclusionJobIdValidator.Validate(
                validation.Config,
                [workflowPath],
                configPath,
                out _);

            await Assert.That(diags.Any(d =>
                d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("unknown job-id 'missing-job'", StringComparison.Ordinal)
                && d.FilePath == configPath)).IsTrue();
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Validate_KnownJobIdInMatchingWorkflow_NoJobIdErrors()
    {
        var dir = CreateRepo(
            workflowYaml: """
            on: push
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            configYaml: """
            exclusions:
              - file: .github/workflows/ci.yml
                jobs:
                  - deploy
                rules:
                  - deny-inherit-secrets
            """);

        try
        {
            var configPath = Path.Combine(dir, ".github", "seiton.yaml");
            var workflowPath = Path.Combine(dir, ".github", "workflows", "ci.yml");
            var validation = LintConfigLibrary.ValidateFile(configPath);

            var diags = ExclusionJobIdValidator.Validate(
                validation.Config,
                [workflowPath],
                configPath,
                out _);

            await Assert.That(diags.Any(d => d.Message.Contains("unknown job-id", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Validate_JobScopedExclusionForOtherFile_NoJobIdErrors()
    {
        var dir = CreateRepo(
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
              - file: .github/workflows/other.yml
                jobs:
                  - missing-job
                rules:
                  - deny-inherit-secrets
            """);

        try
        {
            var configPath = Path.Combine(dir, ".github", "seiton.yaml");
            var workflowPath = Path.Combine(dir, ".github", "workflows", "ci.yml");
            var validation = LintConfigLibrary.ValidateFile(configPath);

            var diags = ExclusionJobIdValidator.Validate(
                validation.Config,
                [workflowPath],
                configPath,
                out _);

            await Assert.That(diags.Any(d => d.Message.Contains("unknown job-id", StringComparison.Ordinal))).IsFalse();
            await Assert.That(diags.Any(d =>
                d.Severity == DiagnosticSeverity.Warning
                && d.Message.Contains("matches no discovered workflow", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Validate_MatchingWorkflow_ParseFailure_NoJobIdError()
    {
        var dir = CreateRepo(
            workflowYaml: """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            broken: [unclosed
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
            var workflowPath = Path.Combine(dir, ".github", "workflows", "ci.yml");
            var validation = LintConfigLibrary.ValidateFile(configPath);

            var diags = ExclusionJobIdValidator.Validate(
                validation.Config,
                [workflowPath],
                configPath,
                out _);

            await Assert.That(diags.Any(d => d.Message.Contains("unknown job-id", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Validate_MatchingWorkflow_EmptyJobsSection_NoJobIdError()
    {
        var dir = CreateRepo(
            workflowYaml: """
            on: push
            jobs: {}
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
            var workflowPath = Path.Combine(dir, ".github", "workflows", "ci.yml");
            var validation = LintConfigLibrary.ValidateFile(configPath);

            var diags = ExclusionJobIdValidator.Validate(
                validation.Config,
                [workflowPath],
                configPath,
                out _);

            await Assert.That(diags.Any(d => d.Message.Contains("unknown job-id", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Validate_GlobMatchesMultipleWorkflows_UnknownJobId_ReportsOncePerJob()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        var workflowsDir = Path.Combine(dir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var workflowYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """;

        File.WriteAllText(Path.Combine(workflowsDir, "ci-a.yml"), workflowYaml, Encoding.UTF8);
        File.WriteAllText(Path.Combine(workflowsDir, "ci-b.yml"), workflowYaml, Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(dir, ".github", "seiton.yaml"),
            """
            exclusions:
              - file: .github/workflows/ci-*.yml
                jobs:
                  - missing-job
                rules:
                  - deny-inherit-secrets
            """,
            Encoding.UTF8);

        try
        {
            var configPath = Path.Combine(dir, ".github", "seiton.yaml");
            var workflowPaths = Directory.GetFiles(workflowsDir).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var validation = LintConfigLibrary.ValidateFile(configPath);

            var diags = ExclusionJobIdValidator.Validate(
                validation.Config,
                workflowPaths,
                configPath,
                out _);

            await Assert.That(diags.Count(d =>
                d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("unknown job-id 'missing-job'", StringComparison.Ordinal))).IsEqualTo(1);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Validate_NoJobScopedExclusions_ReturnsEmpty()
    {
        var dir = CreateRepo(
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
                rules:
                  - deny-inherit-secrets
            """);

        try
        {
            var configPath = Path.Combine(dir, ".github", "seiton.yaml");
            var workflowPath = Path.Combine(dir, ".github", "workflows", "ci.yml");
            var validation = LintConfigLibrary.ValidateFile(configPath);

            var diags = ExclusionJobIdValidator.Validate(
                validation.Config,
                [workflowPath],
                configPath,
                out _);

            await Assert.That(diags.Length).IsEqualTo(0);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    private static string CreateRepo(string workflowYaml, string configYaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        var workflowsDir = Path.Combine(dir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);
        File.WriteAllText(Path.Combine(workflowsDir, "ci.yml"), workflowYaml, Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, ".github", "seiton.yaml"), configYaml, Encoding.UTF8);
        return dir;
    }

    private static void DeleteDirectory(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
