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

    private sealed class RecordingPass(List<string> trace) : IPass
    {
        public void VisitWorkflowPre(Workflow workflow) => trace.Add("workflow-pre");

        public void VisitWorkflowPost(Workflow workflow) => trace.Add("workflow-post");

        public void VisitEvent(Event ev) => trace.Add("event");

        public void VisitJobPre(Job job) => trace.Add("job-pre");

        public void VisitJobPost(Job job) => trace.Add("job-post");

        public void VisitStep(Step step) => trace.Add("step");
    }
}
