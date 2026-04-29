using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags workflow-level <c>permissions: write-all</c> which grants overly broad write access.</summary>
public sealed class DenyWriteAllRule() : RuleBase(RuleId.DenyWriteAll)
{
    public override string Name => "Deny Write-All Rule";

    private Workflow? _currentWorkflow;
    private Job? _currentJob;

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        _currentWorkflow = workflow;
        ValidatePermissionsAll(workflow.Permissions);
        _currentWorkflow = null;
    }

    public override void VisitJobPre(Job job)
    {
        _currentJob = job;
        ValidatePermissionsAll(job.Permissions);
        _currentJob = null;
    }

    private void ValidatePermissionsAll(Permissions? permissions)
    {
        if (Config.Utf8Yaml is null || permissions?.All is null)
        {
            return;
        }

        var allNode = permissions.All;
        var value = Arena.GetStringValue(allNode);
        if (ExpressionScanHelpers.ContainsExpressionMarker(allNode, Arena))
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

        if (_currentWorkflow is not null)
        {
            AddWorkflowError(_currentWorkflow, message, Arena.GetStringRange(allNode), fix);
        }
        else if (_currentJob is not null)
        {
            AddJobError(_currentJob, message, Arena.GetStringRange(allNode), fix);
        }
    }

    private TextEdit BuildEmptyMappingReplacementEdit(StringNodeId allNode, byte[] utf8Yaml)
    {
        var start = Arena.GetStringSlice(allNode).Offset;
        var end = Arena.GetStringSlice(allNode).Offset + Arena.GetStringSlice(allNode).Length;
        if (start < 0 || end > utf8Yaml.Length || start > end)
        {
            return new TextEdit(Arena.GetStringSlice(allNode).Offset, Arena.GetStringSlice(allNode).Length, "{}");
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
