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
        Visit(workflow, skipJobs: null);
    }

    /// <summary>
    /// Traverses the given <paramref name="workflow"/>, optionally skipping jobs by index.
    /// When <paramref name="skipJobs"/>[i] is true, VisitJobPre/VisitStep/VisitJobPost are not called for that job.
    /// VisitWorkflowPre/VisitWorkflowPost are always called (they handle cross-job validation).
    /// </summary>
    internal void Visit(Workflow workflow, bool[]? skipJobs)
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

        var jobIndex = 0;
        foreach (var (_, job) in workflow.Jobs)
        {
            if (skipJobs is not null && (uint)jobIndex < (uint)skipJobs.Length && skipJobs[jobIndex])
            {
                jobIndex++;
                continue;
            }

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

            jobIndex++;
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
