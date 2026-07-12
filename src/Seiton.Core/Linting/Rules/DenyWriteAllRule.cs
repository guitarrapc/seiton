using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags workflow-level <c>permissions: write-all</c> which grants overly broad write access.</summary>
public sealed class DenyWriteAllRule() : RuleBase(RuleId.DenyWriteAll)
{
    public override string Name => "Deny Write-All Rule";

    private WorkflowRef _currentWorkflow;
    private JobRef _currentJob;

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        _currentWorkflow = workflow;
        ValidatePermissionsAll(workflow.Permissions);
        _currentWorkflow = default;
    }

    public override void VisitJobPre(JobRef job)
    {
        _currentJob = job;
        ValidatePermissionsAll(job.Permissions);
        _currentJob = default;
    }

    private void ValidatePermissionsAll(PermissionsRef permissions)
    {
        if (Config.Utf8Yaml is null || !permissions.All.HasValue)
        {
            return;
        }

        var allNode = permissions.All;
        var value = allNode.Value;
        if (ExpressionScanHelpers.ContainsExpressionMarker(allNode.Id, Arena))
        {
            return;
        }

        if (!value.SequenceEqual("write-all"u8))
        {
            return;
        }

        var edit = BuildEmptyMappingReplacementEdit(allNode, Config.Utf8Yaml);
        var fix = new DiagnosticFix(
            "replace write-all with empty permissions",
            [edit]);
        var message = "permissions scalar 'write-all' is forbidden; use explicit least-privilege scopes";

        if (_currentWorkflow.HasValue)
        {
            AddWorkflowError(_currentWorkflow, message, allNode.Range, fix);
        }
        else if (_currentJob.HasValue)
        {
            AddJobError(_currentJob, message, allNode.Range, fix);
        }
    }

    private TextEdit BuildEmptyMappingReplacementEdit(StringRef allNode, byte[] utf8Yaml)
    {
        var start = allNode.Slice.Offset;
        var end = allNode.Slice.Offset + allNode.Slice.Length;
        if (start < 0 || end > utf8Yaml.Length || start > end)
        {
            return new TextEdit(allNode.Slice.Offset, allNode.Slice.Length, "{}");
        }

        if (start > 0 && end < utf8Yaml.Length)
        {
            var before = utf8Yaml[start - 1];
            var after = utf8Yaml[end];
            if ((before == (byte)'\'' && after == (byte)'\'') || (before == (byte)'"' && after == (byte)'"'))
            {
                start--;
                end++;
            }
        }

        if (start < end && end - 1 < utf8Yaml.Length)
        {
            var first = utf8Yaml[start];
            var last = utf8Yaml[end - 1];
            if ((first == (byte)'\'' && last == (byte)'\'') || (first == (byte)'"' && last == (byte)'"'))
            {
                // Range already includes quote chars.
            }
        }

        return new TextEdit(start, end - start, "{}");
    }
}
