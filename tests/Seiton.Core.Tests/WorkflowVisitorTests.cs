using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

public sealed class WorkflowVisitorTests
{
    [Test]
    public async Task Visit_TraversesInExpectedOrder()
    {
        var sourceBytes = Array.Empty<byte>();
        var arena = new AstArena(sourceBytes);

        var (jobs, _) = SliceMapTestExtensions.CreateSliceMap(
            (new Utf8String("build"u8), new Job
            {
                Id = arena.AddString(new Utf8Slice(0, 0), false, default),
                Steps = arena.AddStepIdList(
                [
                    AddRunStep(arena),
                    AddRunStep(arena),
                ]),
            }),
            (new Utf8String("test"u8), new Job
            {
                Id = arena.AddString(new Utf8Slice(0, 0), false, default),
                Steps = arena.AddStepIdList(
                [
                    AddRunStep(arena),
                ]),
            }));

        var workflow = new Workflow
        {
            Jobs = jobs,
        };

        var trace = new List<string>();
        var pass = new RecordingPass(trace);
        var visitor = new WorkflowVisitor();
        visitor.AddPass(pass);

        visitor.Visit(new WorkflowRef(arena, workflow));

        var expected = new[]
        {
            "workflow-pre",
            "job-pre",
            "step",
            "step",
            "job-post",
            "job-pre",
            "step",
            "job-post",
            "workflow-post",
        };

        await Assert.That(trace.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task VisitActionMetadata_TraversesInExpectedOrder()
    {
        var sourceBytes = Array.Empty<byte>();
        var arena = new AstArena(sourceBytes);

        var metadata = new ActionMetadata
        {
            Runs = arena.AddActionMetadataRuns(new ActionMetadataRunsData
            {
                Steps = arena.AddStepIdList(
                [
                    AddRunStep(arena),
                    AddRunStep(arena),
                ]),
            }),
        };

        var trace = new List<string>();
        var pass = new RecordingPass(trace);
        var visitor = new WorkflowVisitor();
        visitor.AddPass(pass);

        visitor.VisitActionMetadata(new ActionMetadataRef(arena, metadata));

        var expected = new[]
        {
            "action-metadata-pre",
            "step",
            "step",
            "action-metadata-post",
        };

        await Assert.That(trace.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task Visit_ParallelStep_TraversesNestedSteps()
    {
        var sourceBytes = Array.Empty<byte>();
        var arena = new AstArena(sourceBytes);

        var nestedRun = AddRunStep(arena);
        var parallelPayload = arena.AddExecParallel(new ExecParallelData
        {
            Steps = arena.AddStepIdList([nestedRun, nestedRun]),
        });
        var parallelStep = arena.AddStep(new StepData
        {
            ExecKind = StepExecKind.Parallel,
            ExecPayload = parallelPayload,
        });
        var job = new Job
        {
            Id = arena.AddString(new Utf8Slice(0, 0), false, default),
            Steps = arena.AddStepIdList([parallelStep]),
        };

        var (jobs, _) = SliceMapTestExtensions.CreateSliceMap(
            (new Utf8String("build"u8), job));

        var workflow = new Workflow { Jobs = jobs };

        var trace = new List<string>();
        var pass = new RecordingPass(trace);
        var visitor = new WorkflowVisitor();
        visitor.AddPass(pass);
        visitor.Visit(new WorkflowRef(arena, workflow));

        var expected = new[]
        {
            "workflow-pre",
            "job-pre",
            "step",
            "step",
            "step",
            "job-post",
            "workflow-post",
        };

        await Assert.That(trace.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task VisitActionMetadata_ParallelStep_TraversesNestedSteps()
    {
        var sourceBytes = Array.Empty<byte>();
        var arena = new AstArena(sourceBytes);

        var nested = AddRunStep(arena);
        var parallelStep = arena.AddStep(new StepData
        {
            ExecKind = StepExecKind.Parallel,
            ExecPayload = arena.AddExecParallel(new ExecParallelData
            {
                Steps = arena.AddStepIdList([nested]),
            }),
        });
        var metadata = new ActionMetadata
        {
            Runs = arena.AddActionMetadataRuns(new ActionMetadataRunsData
            {
                Steps = arena.AddStepIdList([parallelStep]),
            }),
        };

        var trace = new List<string>();
        var pass = new RecordingPass(trace);
        var visitor = new WorkflowVisitor();
        visitor.AddPass(pass);
        visitor.VisitActionMetadata(new ActionMetadataRef(arena, metadata));

        var expected = new[]
        {
            "action-metadata-pre",
            "step",
            "step",
            "action-metadata-post",
        };

        await Assert.That(trace.SequenceEqual(expected)).IsTrue();
    }

    private static StepId AddRunStep(AstArena arena)
    {
        var runPayload = arena.AddExecRun(new ExecRunData
        {
            Run = arena.AddString(new Utf8Slice(0, 0), false, default),
        });
        return arena.AddStep(new StepData
        {
            ExecKind = StepExecKind.Run,
            ExecPayload = runPayload,
        });
    }

    private sealed class RecordingPass(List<string> trace) : IPass
    {
        public void VisitWorkflowPre(WorkflowRef workflow) => trace.Add("workflow-pre");

        public void VisitWorkflowPost(WorkflowRef workflow) => trace.Add("workflow-post");

        public void VisitActionMetadataPre(ActionMetadataRef metadata) => trace.Add("action-metadata-pre");

        public void VisitActionMetadataPost(ActionMetadataRef metadata) => trace.Add("action-metadata-post");

        public void VisitEvent(EventRef ev) => trace.Add("event");

        public void VisitJobPre(JobRef job) => trace.Add("job-pre");

        public void VisitJobPost(JobRef job) => trace.Add("job-post");

        public void VisitStep(StepRef step) => trace.Add("step");
    }
}
