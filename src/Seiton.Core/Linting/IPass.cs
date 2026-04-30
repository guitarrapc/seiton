using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

/// <summary>Visitor callbacks invoked by <see cref="WorkflowVisitor"/> during AST traversal.</summary>
public interface IPass
{
    /// <summary>Called once before traversing the workflow's events, jobs, and steps.</summary>
    public void VisitWorkflowPre(Workflow workflow);

    /// <summary>Called once after all events, jobs, and steps have been traversed.</summary>
    public void VisitWorkflowPost(Workflow workflow);

    /// <summary>
    /// Invoked once before traversing <paramref name="metadata"/> (e.g. <c>runs.steps</c>).
    /// Default: no-op. <see cref="RuleBase"/> clears per-rule diagnostics here, matching <see cref="VisitWorkflowPre"/>.
    /// </summary>
    public void VisitActionMetadataPre(ActionMetadata metadata) { }

    /// <summary>
    /// Invoked once after traversing <paramref name="metadata"/> steps.
    /// </summary>
    public void VisitActionMetadataPost(ActionMetadata metadata) { }

    /// <summary>Called once for each event in the workflow's <c>on:</c> section.</summary>
    public void VisitEvent(Event ev);

    /// <summary>Called before traversing the steps of a job.</summary>
    public void VisitJobPre(Job job);

    /// <summary>Called after all steps of a job have been traversed.</summary>
    public void VisitJobPost(Job job);

    /// <summary>Called once for each step in a job.</summary>
    public void VisitStep(Step step);
}
