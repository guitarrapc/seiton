using Seiton.Core.Parsing.Ast;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.Rules;

public sealed class DenyReadAllRule : RuleBase
{
    public override string Id => "deny-read-all";

    public override string Name => "Deny Read-All Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        ValidatePermissionsAll(
            workflow.Permissions,
            (message, location) => AddWorkflowError(workflow, message, location));
    }

    public override void VisitJobPre(Job job)
    {
        ValidatePermissionsAll(
            job.Permissions,
            (message, location) => AddJobError(job, message, location));
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

        if (!value.SequenceEqual("read-all"u8))
        {
            return;
        }

        report("permissions scalar 'read-all' is forbidden; use explicit least-privilege scopes", allNode.Range);
    }
}
