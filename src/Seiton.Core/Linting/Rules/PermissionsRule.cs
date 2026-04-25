using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates <c>permissions:</c> scope names and access level values.</summary>
public sealed class PermissionsRule() : RuleBase(RuleId.Permissions)
{
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
            if (value.Length == 0)
            {
                AddError("\"\" is invalid for permission for all the scopes. available values are \"read-all\", \"write-all\" or {}", Arena.GetStringRange(permissions.All), workflow, job);
            }
            else if (!string.Equals(value, "read-all", StringComparison.Ordinal)
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
            var scopeName = Decode(scope.NameText);
            var value = Decode(scope.ValueText);

            // Validate scope name
            if (!PermissionScopes.IsKnownScope(scopeName))
            {
                AddError($"unknown permission scope \"{scopeName}\". all available permission scopes are {PermissionScopes.AllScopesList}", Arena.GetStringRange(scope.Name), workflow, job);
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
                    AddError($"\"{value}\" is invalid as permission of scope \"{scopeName}\". available values are {allowedList}", Arena.GetStringRange(scope.Value), workflow, job);
                }
            }
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
