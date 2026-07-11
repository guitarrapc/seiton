using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

/// <summary>Visitor callbacks invoked by <see cref="WorkflowVisitor"/> during AST traversal.</summary>
public interface IPass
{
    /// <summary>Called once before traversing the workflow's events, jobs, and steps.</summary>
    public void VisitWorkflowPre(WorkflowRef workflow);

    /// <summary>Called once after all events, jobs, and steps have been traversed.</summary>
    public void VisitWorkflowPost(WorkflowRef workflow);

    /// <summary>
    /// Invoked once before traversing <paramref name="metadata"/> (e.g. <c>runs.steps</c>).
    /// Default: no-op. <see cref="RuleBase"/> clears per-rule diagnostics here, matching <see cref="VisitWorkflowPre"/>.
    /// </summary>
    public void VisitActionMetadataPre(ActionMetadataRef metadata) { }

    /// <summary>
    /// Invoked once after traversing <paramref name="metadata"/> steps.
    /// </summary>
    public void VisitActionMetadataPost(ActionMetadataRef metadata) { }

    /// <summary>Called once for each event in the workflow's <c>on:</c> section.</summary>
    public void VisitEvent(EventRef ev);

    /// <summary>Called before traversing the steps of a job.</summary>
    public void VisitJobPre(JobRef job);

    /// <summary>Called after all steps of a job have been traversed.</summary>
    public void VisitJobPost(JobRef job);

    /// <summary>Called once for each step in a job.</summary>
    public void VisitStep(StepRef step);
}
