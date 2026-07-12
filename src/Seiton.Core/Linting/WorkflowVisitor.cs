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
    public void Visit(WorkflowRef workflow)
    {
        Visit(workflow, skipJobs: null);
    }

    /// <summary>
    /// Traverses the given <paramref name="workflow"/>, optionally skipping jobs by index.
    /// When <paramref name="skipJobs"/>[i] is true, VisitJobPre/VisitStep/VisitJobPost are not called for that job.
    /// VisitWorkflowPre/VisitWorkflowPost are always called (they handle cross-job validation).
    /// </summary>
    internal void Visit(WorkflowRef workflow, bool[]? skipJobs)
    {
        if (!workflow.HasValue)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        for (var i = 0; i < passes.Count; i++)
        {
            if (passes[i] is RuleBase rule)
            {
                rule.ResetDiagnostics();
            }

            passes[i].VisitWorkflowPre(workflow);
        }

        var on = workflow.On;
        for (var e = 0; e < on.Count; e++)
        {
            var ev = on[e];
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

            var steps = job.Steps;
            for (var s = 0; s < steps.Count; s++)
            {
                VisitStepRecursive(steps[s]);
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
    public void VisitActionMetadata(ActionMetadataRef metadata)
    {
        if (!metadata.HasValue)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        for (var i = 0; i < passes.Count; i++)
        {
            if (passes[i] is RuleBase rule)
            {
                rule.ResetDiagnostics();
            }

            passes[i].VisitActionMetadataPre(metadata);
        }

        var steps = metadata.Runs.Steps;
        for (var s = 0; s < steps.Count; s++)
        {
            VisitStepRecursive(steps[s]);
        }

        for (var i = 0; i < passes.Count; i++)
        {
            passes[i].VisitActionMetadataPost(metadata);
        }
    }

    private void VisitStepRecursive(StepRef step)
    {
        for (var i = 0; i < passes.Count; i++)
        {
            passes[i].VisitStep(step);
        }

        if (step.Exec.Kind == StepExecKind.Parallel)
        {
            var children = step.Exec.AsParallel().Steps;
            for (var s = 0; s < children.Count; s++)
            {
                VisitStepRecursive(children[s]);
            }
        }
    }
}
