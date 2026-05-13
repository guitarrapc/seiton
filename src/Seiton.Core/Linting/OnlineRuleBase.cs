using System.Text;
using Seiton.Core.Linting.OnlineAudit;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting;

/// <summary>
/// Base class for online rules that collect <see cref="ActionAuditTarget"/>
/// during <see cref="WorkflowVisitor"/> traversal and defer evaluation
/// until post-traversal async resolution.
/// </summary>
public abstract class OnlineRuleBase : RuleBase, IOnlineRule
{
    private readonly List<ActionAuditTarget> _targets = [];

    protected OnlineRuleBase(RuleId id) : base(id) { }

    /// <inheritdoc />
    public override bool IsEnabledByDefault => false;

    public IReadOnlyList<ActionAuditTarget> CollectedTargets => _targets;

    public override bool SupportsDocumentKind(DocumentKind documentKind)
        => documentKind == DocumentKind.Workflow;

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        _targets.Clear();
    }

    public override void VisitJobPre(Job job)
    {
        if (job.WorkflowCall is not null)
        {
            TryCollectTarget(job.WorkflowCall.Uses);
        }
    }

    public override void VisitStep(Step step)
    {
        if (step.Exec is ExecAction action)
        {
            TryCollectTarget(action.Uses);
        }
    }

    private void TryCollectTarget(StringNodeId usesNode)
    {
        var usesBytes = Arena.GetStringValue(usesNode);
        if (usesBytes.IsEmpty)
        {
            return;
        }

        var usesText = Encoding.UTF8.GetString(usesBytes);
        if (usesText.StartsWith("./", StringComparison.Ordinal)
            || usesText.StartsWith("docker://", StringComparison.OrdinalIgnoreCase)
            || !TryParseActionReference(usesText, out var owner, out var repo, out var reference))
        {
            return;
        }

        _targets.Add(new ActionAuditTarget(usesText, owner, repo, reference, Arena.GetStringRange(usesNode), Config.FilePath!));
    }

    public abstract void EvaluateTarget(ActionAuditTarget target, ActionAdvisory? advisory, ActionRefResolution? resolution);
}
