using System.Text.Json;
using Seiton.Cli;
using Seiton.Commands;
using Seiton.Output;

namespace Seiton.Tests;

public sealed class FlowJsonFormatTests
{
    private static string CreateWorkflowFile(string yaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "workflow.yml");
        File.WriteAllText(filePath, yaml);
        return filePath;
    }

    private static void DeleteContainingDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [Test]
    [Arguments(OutputFormat.FlowJson)]
    [Arguments(OutputFormat.FlowMermaid)]
    [NotInParallel("Console")]
    public async Task Check_FlowFormat_NoDiscoveredWorkflows_EmitsEmptyFlowDocument(OutputFormat format)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Seiton.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
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
                [directory],
                config: null,
                stdinFilename: "stdin.yml",
                ignore: [],
                minSeverity: null,
                format: format,
                oneline: false,
                color: ColorMode.Never,
                noColor: true,
                verboseLevel: VerboseLevel.Off,
                includeActions: false,
                formatExplicitlySet: true);

            await Assert.That(code).IsEqualTo(ExitCode.Success);
            if (format == OutputFormat.FlowJson)
            {
                using var doc = JsonDocument.Parse(stdout.ToString());
                await Assert.That(doc.RootElement.GetProperty("workflows").GetArrayLength()).IsEqualTo(0);
            }
            else
            {
                await Assert.That(stdout.ToString()).StartsWith("flowchart LR");
            }
        }
        finally
        {
#pragma warning disable TUnit0055
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
#pragma warning restore TUnit0055
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [NotInParallel("Console")]
    public async Task Check_FlowJsonFormat_EmitsFlowDocument()
    {
        var filePath = CreateWorkflowFile(
            """
            name: CI
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - parallel:
                    - run: npm run a
                    - run: npm run b
              deploy:
                runs-on: ubuntu-latest
                needs: build
                steps:
                  - run: echo deploy
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
                format: OutputFormat.FlowJson,
                oneline: false,
                color: ColorMode.Never,
                noColor: true,
                verboseLevel: VerboseLevel.Off,
                includeActions: false,
                formatExplicitlySet: true);

            await Assert.That(code).IsEqualTo(ExitCode.Success);

            using var doc = JsonDocument.Parse(stdout.ToString());
            var root = doc.RootElement;
            await Assert.That(root.GetProperty("version").GetInt32()).IsEqualTo(1);

            var workflow = root.GetProperty("workflows")[0];
            await Assert.That(workflow.GetProperty("name").GetString()).IsEqualTo("CI");

            var jobs = workflow.GetProperty("jobs");
            await Assert.That(jobs.GetArrayLength()).IsEqualTo(2);
            await Assert.That(jobs[0].GetProperty("id").GetString()).IsEqualTo("build");
            await Assert.That(jobs[0].GetProperty("steps")[1].GetProperty("kind").GetString()).IsEqualTo("parallel");
            await Assert.That(jobs[1].GetProperty("needs")[0].GetString()).IsEqualTo("build");
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
    public async Task Check_FlowJsonFormat_NonWorkflowDocument_EmitsEmptyWorkflows()
    {
        var filePath = CreateWorkflowFile(
            """
            name: My Action
            description: does things
            runs:
              using: composite
              steps:
                - run: echo hi
                  shell: bash
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
                format: OutputFormat.FlowJson,
                oneline: false,
                color: ColorMode.Never,
                noColor: true,
                verboseLevel: VerboseLevel.Off,
                includeActions: false,
                formatExplicitlySet: true);

            await Assert.That(code).IsEqualTo(ExitCode.Success);

            using var doc = JsonDocument.Parse(stdout.ToString());
            await Assert.That(doc.RootElement.GetProperty("workflows").GetArrayLength()).IsEqualTo(0);
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
    public async Task Check_FlowMermaidFormat_EmitsFlowchart()
    {
        var filePath = CreateWorkflowFile(
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - run: dotnet build
              deploy:
                runs-on: ubuntu-latest
                needs: build
                steps:
                  - run: echo deploy
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
                format: OutputFormat.FlowMermaid,
                oneline: false,
                color: ColorMode.Never,
                noColor: true,
                verboseLevel: VerboseLevel.Off,
                includeActions: false,
                formatExplicitlySet: true);

            await Assert.That(code).IsEqualTo(ExitCode.Success);

            var output = stdout.ToString();
            await Assert.That(output).Contains("flowchart LR");
            await Assert.That(output).Contains("subgraph j0[\"build\"]");
            await Assert.That(output).Contains("j0 --> j1");
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
    public async Task Fix_FlowMermaidFormat_ReturnsInvalidOptions()
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

            var exitCode = await FixCommand.RunAsync(
                [filePath],
                config: null,
                stdinFilename: "stdin.yml",
                ignore: [],
                minSeverity: null,
                format: OutputFormat.FlowMermaid,
                formatExplicitlySet: true,
                oneline: false,
                color: ColorMode.Never,
                noColor: true,
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: true,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false);

            await Assert.That(exitCode).IsEqualTo(ExitCode.InvalidOptions);
            await Assert.That(stderr.ToString()).Contains("flow-mermaid");
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
    public async Task Fix_FlowJsonFormat_ReturnsInvalidOptions()
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

            var exitCode = await FixCommand.RunAsync(
                [filePath],
                config: null,
                stdinFilename: "stdin.yml",
                ignore: [],
                minSeverity: null,
                format: OutputFormat.FlowJson,
                formatExplicitlySet: true,
                oneline: false,
                color: ColorMode.Never,
                noColor: true,
                verboseLevel: VerboseLevel.Off,
                dryRun: false,
                check: true,
                enablePinNetwork: false,
                enableImageNetwork: false,
                includeActions: false);

            await Assert.That(exitCode).IsEqualTo(ExitCode.InvalidOptions);
            await Assert.That(stderr.ToString()).Contains("flow-json");
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
}
