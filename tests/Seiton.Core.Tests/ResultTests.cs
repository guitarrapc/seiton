using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

public sealed class ResultTests
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
    public async Task Parse_ReturnsParseResult()
    {
        using ParseResult result = WorkflowParser.Parse(SimpleWorkflow, ".github/workflows/test.yml");
        await Assert.That(result.Workflow).IsNotNull();
        await Assert.That(result.HasFatalError).IsFalse();
    }

    [Test]
    public async Task Parse_AstRemainsValidUntilResultDisposed()
    {
        using ParseResult result = WorkflowParser.Parse(SimpleWorkflow, ".github/workflows/test.yml");
        await Assert.That(result.Workflow).IsNotNull();

        var workflow = result.Workflow!;
        var jobs = workflow.Jobs;
        await Assert.That(jobs.Count).IsEqualTo(1);

        var job = jobs.Entries[0].Value;
        var jobIdStr = result.GetString(job.Id);
        await Assert.That(jobIdStr).IsEqualTo("build");
    }

    [Test]
    public async Task ParseResult_GetString_Utf8SliceKey_DecodesMapKeys()
    {
        using ParseResult result = WorkflowParser.Parse(SimpleWorkflow, ".github/workflows/test.yml");

        var jobKey = result.Workflow!.Jobs.Entries[0].Key;
        await Assert.That(result.GetString(jobKey)).IsEqualTo("build");
    }

    [Test]
    public async Task Parse_DiagnosticsAreAccessible()
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
        using ParseResult result = WorkflowParser.Parse(badYaml, ".github/workflows/test.yml");
        await Assert.That(result.Diagnostics.Length).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Parse_ActionMetadata_ReturnsParseResult()
    {
        using ParseResult result = WorkflowParser.Parse(SimpleAction, "action.yml");
        await Assert.That(result.ActionMetadata).IsNotNull();
        await Assert.That(result.Workflow).IsNull();

        var nameStr = result.GetString(result.ActionMetadata!.Name);
        await Assert.That(nameStr).IsEqualTo("My Action");
    }

    [Test]
    public async Task Check_ReturnsLintResult()
    {
        var engine = new LintEngine();
        using LintResult result = engine.Check(SimpleWorkflow, ".github/workflows/test.yml");
        await Assert.That(result.Workflow).IsNotNull();
        await Assert.That(result.HasFatalError).IsFalse();
    }

    [Test]
    public async Task Check_AstAndDiagnosticsAreAccessible()
    {
        var engine = new LintEngine();
        using LintResult result = engine.Check(SimpleWorkflow, ".github/workflows/test.yml");
        await Assert.That(result.Workflow).IsNotNull();

        var workflow = result.Workflow!;
        var job = workflow.Jobs.Entries[0].Value;
        var jobIdStr = result.GetString(job.Id);
        await Assert.That(jobIdStr).IsEqualTo("build");

        await Assert.That(result.DiagnosticCount).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task Check_ResultKeepsHandlesResolvableUntilDispose()
    {
        var engine = new LintEngine();
        using LintResult result = engine.Check(SimpleWorkflow, ".github/workflows/test.yml");
        var workflow = result.Workflow!;
        var jobIdStr = result.GetString(workflow.Jobs.Entries[0].Value.Id);
        await Assert.That(jobIdStr).IsEqualTo("build");
    }

    [Test]
    public async Task LintResult_CopyParseDiagnostics_RemainsValidAfterDispose()
    {
        var yaml = ":\n  ]["u8.ToArray();
        var engine = new LintEngine();

        OwnedDiagnostics parseDiagnostics;
        using (LintResult result = engine.Check(yaml, "test.yml"))
        {
            await Assert.That(result.HasFatalError).IsTrue();
            parseDiagnostics = result.CopyParseDiagnostics();
        }

        await Assert.That(parseDiagnostics.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task ParseResult_CanCrossAsyncBoundary()
    {
        using ParseResult result = await ParseAsync();
        await Assert.That(result.Workflow).IsNotNull();
        var jobIdStr = result.GetString(result.Workflow!.Jobs.Entries[0].Value.Id);
        await Assert.That(jobIdStr).IsEqualTo("build");
    }

    private static async Task<ParseResult> ParseAsync()
    {
        await Task.Yield(); // simulate async work
        return WorkflowParser.Parse(SimpleWorkflow, ".github/workflows/test.yml");
    }

    [Test]
    public async Task LintResult_CanBeStoredInField()
    {
        var holder = new ResultHolder();
        var engine = new LintEngine();
        holder.Result = engine.Check(SimpleWorkflow, ".github/workflows/test.yml");

        using var result = holder.Result;
        await Assert.That(result!.Workflow).IsNotNull();
    }

    [Test]
    public async Task ParseResult_Dispose_ThrowsOnValueAccess()
    {
        ParseResult result = WorkflowParser.Parse(SimpleWorkflow, ".github/workflows/test.yml");
        var jobId = result.Workflow!.Jobs.Entries[0].Value.Id;

        result.Dispose();

        await Assert.That(() => result.GetUtf8(jobId)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ParseResult_Dispose_ThrowsOnAstAndDiagnosticsAccess()
    {
        ParseResult result = WorkflowParser.Parse(SimpleWorkflow, ".github/workflows/test.yml");

        result.Dispose();

        await Assert.That(() => result.Workflow).Throws<ObjectDisposedException>();
        await Assert.That(() => result.Diagnostics).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task LintResult_Dispose_ThrowsOnAstAndDiagnosticsAccess()
    {
        var engine = new LintEngine();
        LintResult result = engine.Check(SimpleWorkflow, ".github/workflows/test.yml");

        result.Dispose();

        await Assert.That(() => result.Workflow).Throws<ObjectDisposedException>();
        await Assert.That(() => result.Diagnostics).Throws<ObjectDisposedException>();
        await Assert.That(() => result.ParseDiagnostics).Throws<ObjectDisposedException>();
        await Assert.That(() => result.DiagnosticCount).Throws<ObjectDisposedException>();
        await Assert.That(() => result.HasFixableDiagnostics).Throws<ObjectDisposedException>();
        await Assert.That(() => result.FixableDiagnosticCount).Throws<ObjectDisposedException>();
        await Assert.That(() => result.FixableDiagnostics).Throws<ObjectDisposedException>();
        await Assert.That(() => result.Fixes).Throws<ObjectDisposedException>();
        await Assert.That(() => result.CopyDiagnostics()).Throws<ObjectDisposedException>();
        await Assert.That(() => result.CopyParseDiagnostics()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ParseResult_Dispose_ThrowsOnCopyDiagnostics()
    {
        ParseResult result = WorkflowParser.Parse(SimpleWorkflow, ".github/workflows/test.yml");

        result.Dispose();

        await Assert.That(() => result.CopyDiagnostics()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task CustomRule_CanResolveNodeValuesWithoutAccessingArena()
    {
        var engine = new LintEngine([new CaptureJobIdRule()]);

        using LintResult result = engine.Check(SimpleWorkflow, ".github/workflows/test.yml");

        await Assert.That(result.DiagnosticCount).IsEqualTo(1);
        var diagnostic = result.Diagnostics[0];
        await Assert.That(diagnostic.Message).IsEqualTo("job id: build");
    }

    [Test]
    public async Task CustomRule_VisitWorkflowPreWithoutBase_DoesNotLeakDiagnosticsAcrossChecks()
    {
        var engine = new LintEngine([new OverrideWorkflowPreWithoutBaseRule()]);

        using var first = engine.Check(SimpleWorkflow, ".github/workflows/test.yml");
        await Assert.That(first.DiagnosticCount).IsEqualTo(1);

        using var second = engine.Check(SimpleWorkflow, ".github/workflows/test.yml");
        await Assert.That(second.DiagnosticCount).IsEqualTo(1);
        await Assert.That(second.Diagnostics[0].Message).IsEqualTo("workflow visited");
    }

    [Test]
    public async Task CustomRule_VisitActionMetadataPreWithoutBase_DoesNotLeakDiagnosticsAcrossChecks()
    {
        var engine = new LintEngine([new OverrideActionMetadataPreWithoutBaseRule()]);

        using var first = engine.Check(SimpleAction, "action.yml");
        await Assert.That(first.DiagnosticCount).IsEqualTo(1);

        using var second = engine.Check(SimpleAction, "action.yml");
        await Assert.That(second.DiagnosticCount).IsEqualTo(1);
        await Assert.That(second.Diagnostics[0].Message).IsEqualTo("action visited");
    }

    private sealed class ResultHolder
    {
        public LintResult? Result { get; set; }
    }

    private sealed class CaptureJobIdRule() : RuleBase(RuleId.JobStructure)
    {
        public override string Name => "capture-job-id";

        public override void VisitJobPre(Job job)
        {
            AddJobInfo(job, $"job id: {GetString(job.Id)}", GetRange(job.Id));
        }
    }

    private sealed class OverrideWorkflowPreWithoutBaseRule() : RuleBase(RuleId.JobStructure)
    {
        public override string Name => "override-workflow-pre-without-base";

        public override void VisitWorkflowPre(Workflow workflow)
        {
            AddWarning("workflow visited", new TextRange(0, 0, 1, 1, 1, 1));
        }
    }

    private sealed class OverrideActionMetadataPreWithoutBaseRule() : RuleBase(RuleId.JobStructure)
    {
        public override string Name => "override-action-pre-without-base";

        public override void VisitActionMetadataPre(ActionMetadata metadata)
        {
            AddWarning("action visited", GetRange(metadata.Name));
        }
    }
}
