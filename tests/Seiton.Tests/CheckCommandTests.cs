using System.Text.Json;
using Seiton.Cli;
using Seiton.Commands;
using Seiton.Output;

namespace Seiton.Tests;

public sealed class CheckCommandTests
{
  [Test]
  [NotInParallel("Console")]
  public async Task Check_GitHubActionsOneline_EmitsGroupedOutputAndDoesNotReturnInvalidOptions()
  {
    var filePath = CreateWorkflowFile(
      """
      on: push
      jobs:
        build:
        runs-on: ubuntu-latest
        steps:
          - run: echo hi
      """);

    var originalOut = Console.Out;
    var originalErr = Console.Error;
    using var stdout = new StringWriter();
    using var stderr = new StringWriter();

    try
    {
#pragma warning disable TUnit0055
      Console.SetOut(stdout);
      Console.SetError(stderr);
#pragma warning restore TUnit0055

      var code = CheckCommand.Run(
        [filePath],
        config: null,
        stdinFilename: "stdin.yml",
        ignore: [],
        minSeverity: null,
        format: OutputFormat.GitHubActions,
        oneline: true,
        color: ColorMode.Never,
        noColor: true,
        verboseLevel: VerboseLevel.Off,
        includeActions: false);

      await Assert.That(code).IsEqualTo(ExitCode.LintIssuesFound);
      await Assert.That(stdout.ToString()).Contains("::group::");
      await Assert.That(stdout.ToString()).Contains("::endgroup::");
      await Assert.That(stdout.ToString()).Contains(": warning [");
    }
    finally
    {
#pragma warning disable TUnit0055
      Console.SetOut(originalOut);
      Console.SetError(originalErr);
#pragma warning restore TUnit0055
      DeleteContainingDirectory(filePath);
    }
  }

    [Test]
    [NotInParallel("Console")]
    public async Task Check_JsonFormat_RunInputsContextDirectUse_Case2ReportsFixableTrue()
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
            on:
              workflow_call:
                inputs:
                  target:
                    required: false
                    type: string
            jobs:
              build:
                runs-on: ubuntu-24.04
                steps:
                  - run: echo "${{ inputs.target }}"
            """);

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
#pragma warning disable TUnit0055
            Console.SetOut(stdout);
            Console.SetError(stderr);
#pragma warning restore TUnit0055

            _ = CheckCommand.Run(
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
                includeActions: false);

            using var json = JsonDocument.Parse(stdout.ToString());
            var diagnostics = json.RootElement;

            JsonElement? target = null;
            for (var i = 0; i < diagnostics.GetArrayLength(); i++)
            {
                var d = diagnostics[i];
                if (d.TryGetProperty("ruleId", out var ruleId)
                    && ruleId.ValueKind == JsonValueKind.String
                    && ruleId.GetString() == "run-inputs-context-direct-use")
                {
                    target = d;
                    break;
                }
            }

            await Assert.That(target.HasValue).IsTrue();
            await Assert.That(target!.Value.GetProperty("fixable").GetBoolean()).IsTrue();
        }
        finally
        {
#pragma warning disable TUnit0055
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
#pragma warning restore TUnit0055
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
