using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public sealed class WorkflowVisitor
{
    readonly List<IPass> passes = [];

    public void AddPass(IPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        passes.Add(pass);
    }

    public void Visit(Workflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        for (var i = 0; i < passes.Count; i++)
        {
            passes[i].VisitWorkflowPre(workflow);
        }

        for (var e = 0; e < workflow.On.Count; e++)
        {
            var ev = workflow.On[e];
            for (var i = 0; i < passes.Count; i++)
            {
                passes[i].VisitEvent(ev);
            }
        }

        foreach (var (_, job) in workflow.Jobs)
        {
            for (var i = 0; i < passes.Count; i++)
            {
                passes[i].VisitJobPre(job);
            }

            if (job.Steps is not null)
            {
                for (var s = 0; s < job.Steps.Count; s++)
                {
                    var step = job.Steps[s];
                    for (var i = 0; i < passes.Count; i++)
                    {
                        passes[i].VisitStep(step);
                    }
                }
            }

            for (var i = 0; i < passes.Count; i++)
            {
                passes[i].VisitJobPost(job);
            }
        }

        for (var i = 0; i < passes.Count; i++)
        {
            passes[i].VisitWorkflowPost(workflow);
        }
    }
}
