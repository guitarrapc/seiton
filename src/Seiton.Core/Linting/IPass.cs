using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public interface IPass
{
    void VisitWorkflowPre(Workflow workflow);

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

    void VisitEvent(Event ev);

    void VisitJobPre(Job job);

    void VisitJobPost(Job job);

    void VisitStep(Step step);
}
