using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

/// <summary>Visitor callbacks invoked by <see cref="WorkflowVisitor"/> during AST traversal.</summary>
public interface IPass
{
    /// <summary>Called once before traversing the workflow's events, jobs, and steps.</summary>
    void VisitWorkflowPre(Workflow workflow);

    /// <summary>Called once after all events, jobs, and steps have been traversed.</summary>
    void VisitWorkflowPost(Workflow workflow);

    /// <summary>
    /// Invoked once before traversing <paramref name="metadata"/> (e.g. <c>runs.steps</c>).
    /// Default: no-op. <see cref="RuleBase"/> clears per-rule diagnostics here, matching <see cref="VisitWorkflowPre"/>.
    /// </summary>
    void VisitActionMetadataPre(ActionMetadata metadata) { }

    /// <summary>
    /// Invoked once after traversing <paramref name="metadata"/> steps.
    /// </summary>
    void VisitActionMetadataPost(ActionMetadata metadata) { }

    /// <summary>Called once for each event in the workflow's <c>on:</c> section.</summary>
    void VisitEvent(Event ev);

    /// <summary>Called before traversing the steps of a job.</summary>
    void VisitJobPre(Job job);

    /// <summary>Called after all steps of a job have been traversed.</summary>
    void VisitJobPost(Job job);

    /// <summary>Called once for each step in a job.</summary>
    void VisitStep(Step step);
}
