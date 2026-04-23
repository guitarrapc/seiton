using Seiton.Core.Linting.OnlineAudit;

namespace Seiton.Core.Linting;

/// <summary>
/// An <see cref="IRule"/> that collects action-reference targets during
/// <see cref="WorkflowVisitor"/> traversal and evaluates them after
/// post-traversal async resolution by <see cref="OnlineAuditEngine"/>.
/// </summary>
public interface IOnlineRule : IRule
{
    /// <summary>
    /// Targets collected during the most recent visitor traversal.
    /// </summary>
    IReadOnlyList<ActionAuditTarget> CollectedTargets { get; }

    /// <summary>
    /// Evaluate a single resolved target and add diagnostics if applicable.
    /// Called by <see cref="OnlineAuditEngine"/> after async resolution.
    /// </summary>
    void EvaluateTarget(ActionAuditTarget target, ActionAdvisory? advisory, ActionRefResolution? resolution);
}
