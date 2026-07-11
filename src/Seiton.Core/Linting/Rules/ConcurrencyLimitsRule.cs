using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Warns when workflows or jobs lack concurrency limits with cancel-in-progress.</summary>
public sealed class ConcurrencyLimitsRule() : RuleBase(RuleId.ConcurrencyLimits)
{
    private bool _isReusableOnly;
    private bool _hasWorkflowConcurrency;

    public override string Name => "Concurrency Limits Rule";

    public override bool SupportsDocumentKind(DocumentKind documentKind) => documentKind == DocumentKind.Workflow;

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        _isReusableOnly = false;
        _hasWorkflowConcurrency = false;

        if (IsReusableOnlyWorkflow(workflow))
        {
            _isReusableOnly = true;
            return;
        }

        if (workflow.Concurrency is { HasValue: true } concurrency)
        {
            _hasWorkflowConcurrency = true;

            if (!concurrency.CancelInProgress.HasValue)
            {
                AddWarning("workflow concurrency is missing 'cancel-in-progress' setting", concurrency.Range);
            }
        }
    }

    public override void VisitJobPre(JobRef job)
    {
        if (_isReusableOnly || _hasWorkflowConcurrency)
        {
            return;
        }

        if (job.WorkflowCall.HasValue)
        {
            return;
        }

        if (!job.Concurrency.HasValue)
        {
            var jobId = job.Id.Decode();
            AddJobWarning(job, $"job '{jobId}' does not declare concurrency settings; consider adding workflow-level concurrency");
        }
        else if (!job.Concurrency.CancelInProgress.HasValue)
        {
            var jobId = job.Id.Decode();
            AddWarning($"job '{jobId}' concurrency is missing 'cancel-in-progress' setting", job.Concurrency.Range);
        }
    }

    private static bool IsReusableOnlyWorkflow(WorkflowRef workflow)
    {
        if (workflow.On.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < workflow.On.Count; i++)
        {
            if (workflow.On[i].Kind != EventKind.WorkflowCall)
            {
                return false;
            }
        }

        return true;
    }
}
