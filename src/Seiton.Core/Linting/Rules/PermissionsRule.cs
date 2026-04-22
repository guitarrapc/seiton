using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class PermissionsRule : RuleBase
{
    public override string Id => "permissions";

    public override string Name => "Permissions Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        ValidatePermissions(workflow.Permissions, workflow, null);
    }

    public override void VisitJobPre(Job job)
    {
        ValidatePermissions(job.Permissions, null, job);
    }

    private void ValidatePermissions(Permissions? permissions, Workflow? workflow, Job? job)
    {
        if (permissions is null)
        {
            return;
        }

        if (permissions.All.HasValue)
        {
            var value = Decode(Arena.GetStringSlice(permissions.All));
            if (!string.Equals(value, "read-all", StringComparison.Ordinal)
                && !string.Equals(value, "write-all", StringComparison.Ordinal))
            {
                AddError($"permissions scalar must be 'read-all' or 'write-all', but got '{value}'", Arena.GetStringRange(permissions.All), workflow, job);
            }
        }

        if (permissions.Scopes is null)
        {
            return;
        }

        foreach (var pair in permissions.Scopes)
        {
            var scope = pair.Value;
            var value = Decode(scope.ValueText);
            if (string.Equals(value, "read", StringComparison.Ordinal)
                || string.Equals(value, "write", StringComparison.Ordinal)
                || string.Equals(value, "none", StringComparison.Ordinal))
            {
                continue;
            }

            var scopeName = Decode(scope.NameText);
            AddError($"permissions.{scopeName} must be one of 'read', 'write', or 'none', but got '{value}'", Arena.GetStringRange(scope.Value), workflow, job);
        }
    }

    private void AddError(string message, TextRange location, Workflow? workflow, Job? job)
    {
        if (job is not null)
        {
            AddJobError(job, message, location);
            return;
        }

        if (workflow is not null)
        {
            AddWorkflowError(workflow, message, location);
        }
    }
}
