using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates <c>permissions:</c> scope names and access level values.</summary>
public sealed class PermissionsRule() : RuleBase(RuleId.Permissions)
{
    public override string Name => "Permissions Rule";

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        ValidatePermissions(workflow.Permissions, workflow, default);
    }

    public override void VisitJobPre(JobRef job)
    {
        ValidatePermissions(job.Permissions, default, job);
    }

    private void ValidatePermissions(PermissionsRef permissions, WorkflowRef workflow, JobRef job)
    {
        if (!permissions.HasValue)
        {
            return;
        }

        if (permissions.All.HasValue)
        {
            var value = permissions.All.Decode();
            if (value.Length == 0)
            {
                AddError("\"\" is invalid for permission for all the scopes. available values are \"read-all\", \"write-all\" or {}", permissions.All.Range, workflow, job);
            }
            else if (!string.Equals(value, "read-all", StringComparison.Ordinal)
                && !string.Equals(value, "write-all", StringComparison.Ordinal))
            {
                AddError($"permissions scalar must be 'read-all' or 'write-all', but got '{value}'", permissions.All.Range, workflow, job);
            }
            else
            {
                var hint = workflow.HasValue
                    ? "use explicit per-scope mapping in each job's permissions instead"
                    : "use explicit per-scope mapping instead";
                AddWarning($"permissions scalar '{value}' is overly broad; {hint}", permissions.All.Range, workflow, job);
            }
        }

        if (!permissions.Scopes.HasValue)
        {
            return;
        }

        foreach (var pair in permissions.Scopes)
        {
            var scope = pair.Value;
            var scopeName = scope.NameText.Decode();
            var value = scope.ValueText.Decode();

            // Validate scope name
            if (!PermissionScopes.IsKnownScope(scopeName))
            {
                AddError($"unknown permission scope \"{scopeName}\". all available permission scopes are {PermissionScopes.AllScopesList}", scope.Name.Range, workflow, job);
                continue;
            }

            // Validate per-scope allowed values
            var allowedValues = PermissionScopes.GetAllowedValues(scopeName);
            if (allowedValues is not null)
            {
                var isAllowed = false;
                for (var i = 0; i < allowedValues.Length; i++)
                {
                    if (string.Equals(value, allowedValues[i], StringComparison.Ordinal))
                    {
                        isAllowed = true;
                        break;
                    }
                }

                if (!isAllowed)
                {
                    var allowedList = string.Join(", ", allowedValues.Select(v => $"\"{v}\""));
                    AddError($"\"{value}\" is invalid as permission of scope \"{scopeName}\". available values are {allowedList}", scope.Value.Range, workflow, job);
                }
            }
        }
    }

    private void AddError(string message, TextRange location, WorkflowRef workflow, JobRef job)
    {
        if (job.HasValue)
        {
            AddJobError(job, message, location);
            return;
        }

        if (workflow.HasValue)
        {
            AddWorkflowError(workflow, message, location);
        }
    }

    private void AddWarning(string message, TextRange location, WorkflowRef workflow, JobRef job)
    {
        if (job.HasValue)
        {
            AddJobWarning(job, message, location);
            return;
        }

        if (workflow.HasValue)
        {
            AddWorkflowWarning(workflow, message, location);
        }
    }
}
