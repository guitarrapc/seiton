using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public sealed class DenyWriteAllRule : RuleBase
{
    public override string Id => "deny-write-all";

    public override string Name => "Deny Write-All Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        ValidatePermissionsAll(workflow.Permissions, (message, location) => AddWorkflowError(workflow, message, location));
    }

    public override void VisitJobPre(Job job)
    {
        ValidatePermissionsAll(job.Permissions, (message, location) => AddJobError(job, message, location));
    }

    void ValidatePermissionsAll(Permissions? permissions, Action<string, TextRange> report)
    {
        if (Config.Utf8Yaml is null || permissions?.All is null)
        {
            return;
        }

        var allNode = permissions.All;
        var value = allNode.Value.AsSpan(Config.Utf8Yaml);
        if (allNode.Expression is not null || value.IndexOf("${{"u8) >= 0)
        {
            return;
        }

        if (!value.SequenceEqual("write-all"u8))
        {
            return;
        }

        report("permissions scalar 'write-all' is forbidden; use least-privilege scopes or 'read-all'", allNode.Range);
    }
}
