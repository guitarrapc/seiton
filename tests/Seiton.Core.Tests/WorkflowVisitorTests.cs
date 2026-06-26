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
                Steps =
                [
                    new Step { Exec = new ExecRun { Kind = StepExecKind.Run, Run = arena.AddString(new Utf8Slice(0, 0), false, default) } },
                    new Step { Exec = new ExecRun { Kind = StepExecKind.Run, Run = arena.AddString(new Utf8Slice(0, 0), false, default) } },
                ],
            }),
            (new Utf8String("test"u8), new Job
            {
                Id = arena.AddString(new Utf8Slice(0, 0), false, default),
                Steps =
                [
                    new Step { Exec = new ExecRun { Kind = StepExecKind.Run, Run = arena.AddString(new Utf8Slice(0, 0), false, default) } },
                ],
            }));

        var workflow = new Workflow
        {
            Jobs = jobs,
        };

        var trace = new List<string>();
        var pass = new RecordingPass(trace);
        var visitor = new WorkflowVisitor();
        visitor.AddPass(pass);

        visitor.Visit(workflow);

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
            Runs = new ActionMetadataRuns
            {
                Steps =
                [
                    new Step { Exec = new ExecRun { Kind = StepExecKind.Run, Run = arena.AddString(new Utf8Slice(0, 0), false, default) } },
                    new Step { Exec = new ExecRun { Kind = StepExecKind.Run, Run = arena.AddString(new Utf8Slice(0, 0), false, default) } },
                ],
            },
        };

        var trace = new List<string>();
        var pass = new RecordingPass(trace);
        var visitor = new WorkflowVisitor();
        visitor.AddPass(pass);

        visitor.VisitActionMetadata(metadata);

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

        var nestedRun = new Step
        {
            Exec = new ExecRun
            {
                Kind = StepExecKind.Run,
                Run = arena.AddString(new Utf8Slice(0, 0), false, default),
            },
        };
        var parallel = new ExecParallel
        {
            Kind = StepExecKind.Parallel,
            Steps = [nestedRun, nestedRun],
        };
        var job = new Job
        {
            Id = arena.AddString(new Utf8Slice(0, 0), false, default),
            Steps =
            [
                new Step { Exec = parallel },
            ],
        };

        var (jobs, _) = SliceMapTestExtensions.CreateSliceMap(
            (new Utf8String("build"u8), job));

        var workflow = new Workflow { Jobs = jobs };

        var trace = new List<string>();
        var pass = new RecordingPass(trace);
        var visitor = new WorkflowVisitor();
        visitor.AddPass(pass);
        visitor.Visit(workflow);

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

        var nested = new Step
        {
            Exec = new ExecRun
            {
                Kind = StepExecKind.Run,
                Run = arena.AddString(new Utf8Slice(0, 0), false, default),
            },
        };
        var metadata = new ActionMetadata
        {
            Runs = new ActionMetadataRuns
            {
                Steps =
                [
                    new Step
                    {
                        Exec = new ExecParallel
                        {
                            Kind = StepExecKind.Parallel,
                            Steps = [nested],
                        },
                    },
                ],
            },
        };

        var trace = new List<string>();
        var pass = new RecordingPass(trace);
        var visitor = new WorkflowVisitor();
        visitor.AddPass(pass);
        visitor.VisitActionMetadata(metadata);

        var expected = new[]
        {
            "action-metadata-pre",
            "step",
            "step",
            "action-metadata-post",
        };

        await Assert.That(trace.SequenceEqual(expected)).IsTrue();
    }

    private sealed class RecordingPass(List<string> trace) : IPass
    {
        public void VisitWorkflowPre(Workflow workflow) => trace.Add("workflow-pre");

        public void VisitWorkflowPost(Workflow workflow) => trace.Add("workflow-post");

        public void VisitActionMetadataPre(ActionMetadata metadata) => trace.Add("action-metadata-pre");

        public void VisitActionMetadataPost(ActionMetadata metadata) => trace.Add("action-metadata-post");

        public void VisitEvent(Event ev) => trace.Add("event");

        public void VisitJobPre(Job job) => trace.Add("job-pre");

        public void VisitJobPost(Job job) => trace.Add("job-post");

        public void VisitStep(Step step) => trace.Add("step");
    }
}
