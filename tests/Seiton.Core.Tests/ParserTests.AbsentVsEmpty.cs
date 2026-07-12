using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

// Absent-vs-empty recovery semantics (design doc §3.1): a key absent in YAML must be
// distinguishable from a key present with an empty value. Absent → HasValue false;
// present-empty → HasValue true && Count 0.
public sealed partial class ParserTests
{
    private static ParseResult ParseWorkflow(string yaml, string filePath = "wf.yml")
        => WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml.Replace("\r\n", "\n")), filePath);

    [Test]
    public async Task Parse_JobSteps_AbsentVsEmpty_ShapesDiffer()
    {
        var yaml = """
        on: push
        jobs:
          absent:
            uses: octo/repo/.github/workflows/x.yml@v1
          empty:
            runs-on: ubuntu-latest
            steps: []
        """;

        using var result = ParseWorkflow(yaml);
        result.Workflow.Jobs.TryGetValue("absent"u8, out var absentJob);
        result.Workflow.Jobs.TryGetValue("empty"u8, out var emptyJob);

        await Assert.That(absentJob.Steps.HasValue).IsFalse();
        await Assert.That(absentJob.Steps.Count).IsEqualTo(0);
        await Assert.That(emptyJob.Steps.HasValue).IsTrue();
        await Assert.That(emptyJob.Steps.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_StepWith_AbsentVsEmpty_ShapesDiffer()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v4
              - uses: actions/checkout@v4
                with: {}
        """;

        using var result = ParseWorkflow(yaml);
        result.Workflow.Jobs.TryGetValue("build"u8, out var job);

        var absentWith = job.Steps[0].Exec.AsAction().Inputs;
        var emptyWith = job.Steps[1].Exec.AsAction().Inputs;

        await Assert.That(absentWith.HasValue).IsFalse();
        await Assert.That(absentWith.Count).IsEqualTo(0);
        await Assert.That(emptyWith.HasValue).IsTrue();
        await Assert.That(emptyWith.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_JobOutputs_AbsentVsEmpty_ShapesDiffer()
    {
        var yaml = """
        on: push
        jobs:
          absent:
            runs-on: ubuntu-latest
            steps:
              - run: echo a
          empty:
            runs-on: ubuntu-latest
            outputs: {}
            steps:
              - run: echo b
        """;

        using var result = ParseWorkflow(yaml);
        result.Workflow.Jobs.TryGetValue("absent"u8, out var absentJob);
        result.Workflow.Jobs.TryGetValue("empty"u8, out var emptyJob);

        await Assert.That(absentJob.Outputs.HasValue).IsFalse();
        await Assert.That(absentJob.Outputs.Count).IsEqualTo(0);
        await Assert.That(emptyJob.Outputs.HasValue).IsTrue();
        await Assert.That(emptyJob.Outputs.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_WorkflowCallSecrets_AbsentVsEmpty_ShapesDiffer()
    {
        var absentYaml = """
        on:
          workflow_call:
            inputs:
              name:
                type: string
        jobs: {}
        """;
        var emptyYaml = """
        on:
          workflow_call:
            secrets: {}
        jobs: {}
        """;

        using var absentResult = ParseWorkflow(absentYaml);
        using var emptyResult = ParseWorkflow(emptyYaml);

        var absentSecrets = absentResult.Workflow.On[0].AsWorkflowCall().Secrets;
        var emptySecrets = emptyResult.Workflow.On[0].AsWorkflowCall().Secrets;

        await Assert.That(absentSecrets.HasValue).IsFalse();
        await Assert.That(absentSecrets.Count).IsEqualTo(0);
        await Assert.That(emptySecrets.HasValue).IsTrue();
        await Assert.That(emptySecrets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_ActionMetadataInputs_AbsentVsEmpty_ShapesDiffer()
    {
        var absentYaml = """
        name: my-action
        description: does things
        runs:
          using: node20
          main: index.js
        """;
        var emptyYaml = """
        name: my-action
        description: does things
        inputs: {}
        runs:
          using: node20
          main: index.js
        """;

        using var absentResult = ParseWorkflow(absentYaml, "action.yml");
        using var emptyResult = ParseWorkflow(emptyYaml, "action.yml");

        var absentInputs = absentResult.ActionMetadata.Inputs;
        var emptyInputs = emptyResult.ActionMetadata.Inputs;

        await Assert.That(absentInputs.HasValue).IsFalse();
        await Assert.That(absentInputs.Count).IsEqualTo(0);
        await Assert.That(emptyInputs.HasValue).IsTrue();
        await Assert.That(emptyInputs.Count).IsEqualTo(0);
    }
}
