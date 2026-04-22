using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class GitHubAppTokenInputsRule : RuleBase
{
    public override string Id => "github-app-token-inputs";

    public override string Name => "GitHub App Token Inputs Rule";

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = actionExec.Uses.Value.AsSpan(Config.Utf8Yaml);
        if (!IsCreateGitHubAppTokenAction(uses))
        {
            return;
        }

        var hasRepositoryConstraint = false;
        var hasPermissionConstraint = false;
        var hasOwner = false;
        if (actionExec.Inputs is not null)
        {
            foreach (var pair in actionExec.Inputs.Value)
            {
                var key = pair.Key.AsSpan(Config.Utf8Yaml);
                if (IsRepositoryConstraintKey(key))
                {
                    hasRepositoryConstraint = true;
                }

                if (IsPermissionConstraintKey(key))
                {
                    hasPermissionConstraint = true;
                }

                if (IsOwnerKey(key))
                {
                    hasOwner = true;
                }
            }
        }

        if (!hasOwner)
        {
            // create-github-app-token defaults to the current repository when neither
            // owner nor repositories are specified.
            hasRepositoryConstraint = true;
        }

        if (hasRepositoryConstraint && hasPermissionConstraint)
        {
            return;
        }

        var usesText = Decode(actionExec.Uses.Value);
        var usesLocation = BuildUsesLocation(actionExec);
        if (!hasRepositoryConstraint && !hasPermissionConstraint)
        {
            AddStepError(
                step,
                $"action '{usesText}' should set repository and permission constraints when minting GitHub App token (expected repositories when owner is set, plus with.permissions or with.permission-*)",
                usesLocation);
            return;
        }

        if (!hasRepositoryConstraint)
        {
            AddStepError(
                step,
                $"action '{usesText}' should set repository constraints when owner is set for GitHub App token (expected with.repositories or with.repository)",
                usesLocation);
            return;
        }

        AddStepError(
            step,
            $"action '{usesText}' should set permission constraints for GitHub App token (expected with.permissions or with.permission-*)",
            usesLocation);
    }

    private static bool IsCreateGitHubAppTokenAction(ReadOnlySpan<byte> uses)
    {
        if (uses.IsEmpty || uses.StartsWith("./"u8) || uses.StartsWith("../"u8) || uses.StartsWith("docker://"u8))
        {
            return false;
        }

        return MatchesActionReference(uses, "actions/create-github-app-token"u8);
    }

    private static bool MatchesActionReference(ReadOnlySpan<byte> uses, ReadOnlySpan<byte> actionName)
    {
        if (uses.Length < actionName.Length)
        {
            return false;
        }

        if (!EqualsAsciiIgnoreCase(uses[..actionName.Length], actionName))
        {
            return false;
        }

        return uses.Length == actionName.Length || uses[actionName.Length] == (byte)'@';
    }

    private static bool IsRepositoryConstraintKey(ReadOnlySpan<byte> inputKey)
    {
        return EqualsAsciiIgnoreCase(inputKey, "repositories"u8)
            || EqualsAsciiIgnoreCase(inputKey, "repository"u8);
    }

    private static bool IsOwnerKey(ReadOnlySpan<byte> inputKey)
    {
        return EqualsAsciiIgnoreCase(inputKey, "owner"u8);
    }

    private static bool IsPermissionConstraintKey(ReadOnlySpan<byte> inputKey)
    {
        return EqualsAsciiIgnoreCase(inputKey, "permissions"u8)
            || StartsWithAsciiIgnoreCase(inputKey, "permission-"u8);
    }

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> prefix)
    {
        if (left.Length < prefix.Length)
        {
            return false;
        }

        return EqualsAsciiIgnoreCase(left[..prefix.Length], prefix);
    }
}
