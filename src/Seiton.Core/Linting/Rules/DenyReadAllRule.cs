using Seiton.Core.Parsing.Ast;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.Rules;

public sealed class DenyReadAllRule() : RuleBase(RuleId.DenyReadAll)
{
    public override string Name => "Deny Read-All Rule";

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
        if (Arena.GetStringExpression(allNode).HasValue || value.IndexOf("${{"u8) >= 0)
        {
            return;
        }

        if (!value.SequenceEqual("read-all"u8))
        {
            return;
        }

        var edit = BuildExplicitMappingReplacementEdit(allNode, Config.Utf8Yaml);
        var fix = new DiagnosticFix(
            "replace read-all with explicit permissions mapping baseline",
            [edit]);
        var message = "permissions scalar 'read-all' is forbidden; use explicit least-privilege scopes";

        if (_currentWorkflow is not null)
        {
            AddWorkflowError(_currentWorkflow, message, Arena.GetStringRange(allNode), fix);
        }
        else if (_currentJob is not null)
        {
            AddJobError(_currentJob, message, Arena.GetStringRange(allNode), fix);
        }
    }

    private TextEdit BuildExplicitMappingReplacementEdit(StringNodeId allNode, byte[] utf8Yaml)
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
