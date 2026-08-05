using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

public sealed partial class ParserTests
{
    [Test]
    public async Task Parse_StepSelfRepositoryUses_ClassifiesReference()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: $/.github/actions/hello-world-action
        """;

        using var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "self-repository.yml");

        result.Workflow.Jobs.TryGetValue("build"u8, out var job);
        var action = job.Steps[0].Exec.AsAction();
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(action.Uses.Decode()).IsEqualTo("$/.github/actions/hello-world-action");
        await Assert.That(action.IsSelfRepositoryReference).IsTrue();
    }

    [Test]
    public async Task Parse_SelfRepositoryUsesAcrossWorkflowContexts_ClassifiesReferences()
    {
        var yaml = """
        on: push
        jobs:
          call:
            uses: $/.github/workflows/reusable.yml
          build:
            runs-on: ubuntu-latest
            steps:
              - parallel:
                  - uses: $/.github/actions/nested-action
        """;

        using var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "self-repository-contexts.yml");

        result.Workflow.Jobs.TryGetValue("call"u8, out var callJob);
        result.Workflow.Jobs.TryGetValue("build"u8, out var buildJob);
        var nestedAction = buildJob.Steps[0].Exec.AsParallel().Steps[0].Exec.AsAction();

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(callJob.WorkflowCall.Uses.Decode()).IsEqualTo("$/.github/workflows/reusable.yml");
        await Assert.That(callJob.WorkflowCall.IsSelfRepositoryReference).IsTrue();
        await Assert.That(nestedAction.IsSelfRepositoryReference).IsTrue();
    }

    [Test]
    public async Task Parse_CompositeActionSelfRepositoryUses_ClassifiesReference()
    {
        var yaml = """
        name: composite
        description: composite action
        runs:
          using: composite
          steps:
            - uses: $/.github/actions/sibling-action
        """;

        using var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "action.yml");

        var action = result.ActionMetadata.Runs.Steps[0].Exec.AsAction();
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(action.IsSelfRepositoryReference).IsTrue();
    }

    [Test]
    public async Task Parse_OtherUsesReferences_DoNotClassifyAsSelfRepository()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: ./.github/actions/local-action
              - uses: owner/repository/action@v1
              - uses: $not/.github/actions/action
        """;

        using var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "other-references.yml");

        result.Workflow.Jobs.TryGetValue("build"u8, out var job);
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(job.Steps[0].Exec.AsAction().IsSelfRepositoryReference).IsFalse();
        await Assert.That(job.Steps[1].Exec.AsAction().IsSelfRepositoryReference).IsFalse();
        await Assert.That(job.Steps[2].Exec.AsAction().IsSelfRepositoryReference).IsFalse();
        await Assert.That(default(ExecActionRef).IsSelfRepositoryReference).IsFalse();
        await Assert.That(default(WorkflowCallRef).IsSelfRepositoryReference).IsFalse();
    }
}
