#if DEBUG
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using System.Text;

namespace Seiton.Core.Tests;

// Stage 4 DEBUG generation counter: resolving any AST handle/ref after its AstArena
// has been reset or disposed must throw immediately instead of silently returning
// another parse's data. The checks only exist in Debug builds, so this whole file
// is compiled under #if DEBUG.
public sealed class ArenaGenerationTests
{
    private const string Yaml =
        """
        name: ci
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - name: checkout
                uses: actions/checkout@v4
        """;

    [Test]
    public async Task ReadingBeforeDispose_Works()
    {
        using var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(Yaml), "wf.yml");

        var workflow = result.Workflow;
        await Assert.That(workflow.HasValue).IsTrue();
        await Assert.That(workflow.Name.Decode()).IsEqualTo("ci");

        var found = workflow.Jobs.TryGetValue("build"u8, out var job);
        await Assert.That(found).IsTrue();
        await Assert.That(job.Steps.Count).IsEqualTo(1);

        var step = job.Steps[0];
        await Assert.That(step.Name.Decode()).IsEqualTo("checkout");
        await Assert.That(result.Diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task AccessAfterDispose_Throws()
    {
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(Yaml), "wf.yml");

        var workflow = result.Workflow;
        workflow.Jobs.TryGetValue("build"u8, out var job);
        StepRef step = job.Steps[0];

        result.Dispose();

        // Result-level accessors throw (ObjectDisposedException derives from InvalidOperationException).
        Assert.Throws<InvalidOperationException>(() => _ = result.Workflow);
        Assert.Throws<InvalidOperationException>(() => _ = result.Diagnostics);

        // Stale refs captured before dispose throw the generation check.
        Assert.Throws<InvalidOperationException>(() => _ = workflow.Name.HasText);
        Assert.Throws<InvalidOperationException>(() => _ = step.Name.HasText);
        Assert.Throws<InvalidOperationException>(() => _ = step.Range);
        Assert.Throws<InvalidOperationException>(() => _ = job.Steps.Count > 0 && job.Steps[0].HasValue);

        await Task.CompletedTask;
    }

    [Test]
    public async Task HasValueOnStaleRef_DoesNotThrow()
    {
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(Yaml), "wf.yml");

        var workflow = result.Workflow;
        workflow.Jobs.TryGetValue("build"u8, out var job);
        var steps = job.Steps;
        var step = steps[0];
        var name = step.Name;

        result.Dispose();

        // HasValue must stay safe on stale refs (it never dereferences arena data).
        await Assert.That(workflow.HasValue).IsTrue();
        await Assert.That(job.HasValue).IsTrue();
        await Assert.That(steps.HasValue).IsTrue();
        await Assert.That(step.HasValue).IsTrue();
        await Assert.That(name.HasValue).IsTrue();
    }
}
#endif
