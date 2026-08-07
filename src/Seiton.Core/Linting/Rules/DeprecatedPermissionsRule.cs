using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>
/// Warns on <c>permissions:</c> scopes GitHub has retired. Such scopes are still accepted by
/// GitHub Actions, so <see cref="PermissionsRule"/> keeps treating them as valid; this rule
/// prompts their removal.
/// </summary>
public sealed class DeprecatedPermissionsRule() : RuleBase(RuleId.DeprecatedPermissions)
{
    public override string Name => "Deprecated Permissions Rule";

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        CheckPermissions(workflow.Permissions, workflow, default);
    }

    public override void VisitJobPre(JobRef job)
    {
        CheckPermissions(job.Permissions, default, job);
    }

    private void CheckPermissions(PermissionsRef permissions, WorkflowRef workflow, JobRef job)
    {
        if (!permissions.HasValue || !permissions.Scopes.HasValue)
        {
            return;
        }

        foreach (var pair in permissions.Scopes)
        {
            var scope = pair.Value;
            var note = PermissionScopes.GetDeprecationNote(scope.NameText.Bytes);
            if (note is null)
            {
                continue;
            }

            var message = $"permission scope \"{scope.NameText.Decode()}\" was deprecated. {note}";
            if (job.HasValue)
            {
                AddJobWarning(job, message, scope.Name.Range);
            }
            else if (workflow.HasValue)
            {
                AddWorkflowWarning(workflow, message, scope.Name.Range);
            }
        }
    }
}
