using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

/// <summary>
/// Walks the workflow/action metadata AST and dispatches events to registered <see cref="IPass"/> implementations.
/// </summary>
public sealed class WorkflowVisitor
{
    private readonly List<IPass> passes = [];

    /// <summary>Registers a pass to be invoked during visitor traversal.</summary>
    public void AddPass(IPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        passes.Add(pass);
    }

    /// <summary>Removes all registered passes.</summary>
    public void Reset()
    {
        passes.Clear();
    }

    /// <summary>Traverses the given <paramref name="workflow"/>, invoking all registered passes for each event, job, and step.</summary>
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

    /// <summary>Traverses the given action <paramref name="metadata"/>, invoking all registered passes for each step in <c>runs.steps</c>.</summary>
    public void VisitActionMetadata(ActionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        for (var i = 0; i < passes.Count; i++)
        {
            passes[i].VisitActionMetadataPre(metadata);
        }

        var steps = metadata.Runs?.Steps;
        if (steps is not null)
        {
            for (var s = 0; s < steps.Count; s++)
            {
                var step = steps[s];
                for (var i = 0; i < passes.Count; i++)
                {
                    passes[i].VisitStep(step);
                }
            }
        }

        for (var i = 0; i < passes.Count; i++)
        {
            passes[i].VisitActionMetadataPost(metadata);
        }
    }
}
