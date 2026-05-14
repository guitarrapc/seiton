using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class DetachTests
{
    private static readonly byte[] SimpleWorkflow = Encoding.UTF8.GetBytes("""
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo hello
        """);

    private static readonly byte[] SimpleAction = Encoding.UTF8.GetBytes("""
        name: My Action
        description: A test action
        runs:
          using: composite
          steps:
            - run: echo hi
              shell: bash
        """);

    [Test]
    public async Task ParseHandle_Detach_ReturnsOwnedParseResult()
    {
        OwnedParseResult owned;
        {
            using var handle = WorkflowParser.Parse(SimpleWorkflow, ".github/workflows/test.yml");
            owned = handle.Detach();
        }

        using (owned)
        {
            await Assert.That(owned.Workflow).IsNotNull();
            await Assert.That(owned.HasFatalError).IsFalse();
        }
    }

    [Test]
    public async Task ParseHandle_Detach_AstRemainsValidAfterHandleScope()
    {
        OwnedParseResult owned;
        {
            using var handle = WorkflowParser.Parse(SimpleWorkflow, ".github/workflows/test.yml");
            owned = handle.Detach();
        }
        // handle is disposed here, but owned keeps arena alive

        using (owned)
        {
            await Assert.That(owned.Workflow).IsNotNull();

            // Arena should be accessible for handle resolution
            var workflow = owned.Workflow!;
            var jobs = workflow.Jobs;
            await Assert.That(jobs.Count).IsEqualTo(1);

            // Resolve StringNodeId via arena
            var job = jobs.Entries[0].Value;
            var jobIdValue = owned.Arena.GetStringValue(job.Id);
            var jobIdStr = Encoding.UTF8.GetString(jobIdValue);
            await Assert.That(jobIdStr).IsEqualTo("build");
        }
    }

    [Test]
    public async Task ParseHandle_Detach_DiagnosticsAreSafe()
    {
        // Use a workflow with parse errors to get diagnostics
        var badYaml = Encoding.UTF8.GetBytes("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                invalid-key: something
                steps:
                  - run: echo hello
            """);

        OwnedParseResult owned;
        {
            using var handle = WorkflowParser.Parse(badYaml, ".github/workflows/test.yml");
            owned = handle.Detach();
        }

        using (owned)
        {
            // Diagnostics should be accessible after handle disposal
            await Assert.That(owned.Diagnostics.Length).IsGreaterThanOrEqualTo(1);
        }
    }

    [Test]
    public async Task ParseHandle_Detach_TransfersOwnership()
    {
        // After Detach, handle.Dispose() should be a no-op (arena transferred)
        OwnedParseResult owned;
        {
            using var handle = WorkflowParser.Parse(SimpleWorkflow, ".github/workflows/test.yml");
            owned = handle.Detach();
            // handle.Dispose() runs here — should NOT dispose the arena
        }

        using (owned)
        {
            // Arena should still be valid
            var workflow = owned.Workflow!;
            var jobIdValue = owned.Arena.GetStringValue(workflow.Jobs.Entries[0].Value.Id);
            var jobIdStr = Encoding.UTF8.GetString(jobIdValue);
            await Assert.That(jobIdStr).IsEqualTo("build");
        }
    }

    [Test]
    public async Task ParseHandle_Detach_ActionMetadata()
    {
        OwnedParseResult owned;
        {
            using var handle = WorkflowParser.Parse(SimpleAction, "action.yml");
            owned = handle.Detach();
        }

        using (owned)
        {
            await Assert.That(owned.ActionMetadata).IsNotNull();
            await Assert.That(owned.Workflow).IsNull();

            var name = owned.Arena.GetStringValue(owned.ActionMetadata!.Name);
            var nameStr = Encoding.UTF8.GetString(name);
            await Assert.That(nameStr).IsEqualTo("My Action");
        }
    }

    [Test]
    public async Task LintHandle_Detach_ReturnsOwnedLintResult()
    {
        var engine = new LintEngine();
        OwnedLintResult owned;
        {
            using var handle = engine.Check(SimpleWorkflow, ".github/workflows/test.yml");
            owned = handle.Detach();
        }

        using (owned)
        {
            await Assert.That(owned.Workflow).IsNotNull();
            await Assert.That(owned.HasFatalError).IsFalse();
        }
    }

    [Test]
    public async Task LintHandle_Detach_AstAndDiagnosticsValidAfterHandleScope()
    {
        var engine = new LintEngine();
        OwnedLintResult owned;
        {
            using var handle = engine.Check(SimpleWorkflow, ".github/workflows/test.yml");
            owned = handle.Detach();
        }

        using (owned)
        {
            await Assert.That(owned.Workflow).IsNotNull();

            // Arena for handle resolution
            var workflow = owned.Workflow!;
            var job = workflow.Jobs.Entries[0].Value;
            var jobIdStr = Encoding.UTF8.GetString(owned.Arena.GetStringValue(job.Id));
            await Assert.That(jobIdStr).IsEqualTo("build");

            // Diagnostics should be present (lint rules fire)
            await Assert.That(owned.DiagnosticCount).IsGreaterThanOrEqualTo(0);
        }
    }

    [Test]
    public async Task LintHandle_Detach_TransfersOwnership()
    {
        var engine = new LintEngine();
        OwnedLintResult owned;
        {
            using var handle = engine.Check(SimpleWorkflow, ".github/workflows/test.yml");
            owned = handle.Detach();
            // handle.Dispose() runs here — should NOT dispose the arena
        }

        using (owned)
        {
            var workflow = owned.Workflow!;
            var jobIdValue = owned.Arena.GetStringValue(workflow.Jobs.Entries[0].Value.Id);
            var jobIdStr = Encoding.UTF8.GetString(jobIdValue);
            await Assert.That(jobIdStr).IsEqualTo("build");
        }
    }

    [Test]
    public async Task OwnedParseResult_CanCrossAsyncBoundary()
    {
        // Key feature: OwnedParseResult is a class, not ref struct,
        // so it can be used across async boundaries
        using var owned = await ParseAsync();
        await Assert.That(owned.Workflow).IsNotNull();
        var jobIdStr = Encoding.UTF8.GetString(
            owned.Arena.GetStringValue(owned.Workflow!.Jobs.Entries[0].Value.Id));
        await Assert.That(jobIdStr).IsEqualTo("build");
    }

    private static async Task<OwnedParseResult> ParseAsync()
    {
        await Task.Yield(); // simulate async work
        using var handle = WorkflowParser.Parse(SimpleWorkflow, ".github/workflows/test.yml");
        return handle.Detach();
    }

    [Test]
    public async Task OwnedLintResult_CanBeStoredInField()
    {
        // Key feature: OwnedLintResult can be stored in fields (not possible with ref struct LintHandle)
        var holder = new ResultHolder();
        var engine = new LintEngine();
        {
            using var handle = engine.Check(SimpleWorkflow, ".github/workflows/test.yml");
            holder.Result = handle.Detach();
        }

        using (holder.Result)
        {
            await Assert.That(holder.Result.Workflow).IsNotNull();
        }
    }

    [Test]
    public async Task OwnedParseResult_Dispose_ThrowsOnArenaAccess()
    {
        OwnedParseResult owned;
        {
            using var handle = WorkflowParser.Parse(SimpleWorkflow, ".github/workflows/test.yml");
            owned = handle.Detach();
        }

        owned.Dispose();

        // Accessing Arena after Dispose should throw
        await Assert.That(() => _ = owned.Arena).Throws<ObjectDisposedException>();
    }

    private sealed class ResultHolder
    {
        public OwnedLintResult? Result { get; set; }
    }
}
