using Seiton.Core.Parsing.Ast;

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
        if (!IsGitHubAppTokenAction(uses))
        {
            return;
        }

        var hasRepositoryConstraint = false;
        var hasPermissionConstraint = false;
        if (actionExec.Inputs is not null)
        {
            foreach (var pair in actionExec.Inputs)
            {
                var key = pair.Key.Span;
                if (IsRepositoryConstraintKey(key))
                {
                    hasRepositoryConstraint = true;
                }

                if (IsPermissionConstraintKey(key))
                {
                    hasPermissionConstraint = true;
                }
            }
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
                $"action '{usesText}' should set repository and permission constraints when minting GitHub App token (expected with.repositories/repository and with.permissions or with.permission-*)",
                usesLocation);
            return;
        }

        if (!hasRepositoryConstraint)
        {
            AddStepError(
                step,
                $"action '{usesText}' should set repository constraints for GitHub App token (expected with.repositories or with.repository)",
                usesLocation);
            return;
        }

        AddStepError(
            step,
            $"action '{usesText}' should set permission constraints for GitHub App token (expected with.permissions or with.permission-*)",
            usesLocation);
    }

    static bool IsGitHubAppTokenAction(ReadOnlySpan<byte> uses)
    {
        if (uses.IsEmpty || uses.StartsWith("./"u8) || uses.StartsWith("../"u8) || uses.StartsWith("docker://"u8))
        {
            return false;
        }

        return MatchesActionReference(uses, "actions/create-github-app-token"u8)
            || MatchesActionReference(uses, "tibdex/github-app-token"u8);
    }

    static bool MatchesActionReference(ReadOnlySpan<byte> uses, ReadOnlySpan<byte> actionName)
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

    static bool IsRepositoryConstraintKey(ReadOnlySpan<byte> inputKey)
    {
        return EqualsAsciiIgnoreCase(inputKey, "repositories"u8)
            || EqualsAsciiIgnoreCase(inputKey, "repository"u8);
    }

    static bool IsPermissionConstraintKey(ReadOnlySpan<byte> inputKey)
    {
        return EqualsAsciiIgnoreCase(inputKey, "permissions"u8)
            || StartsWithAsciiIgnoreCase(inputKey, "permission-"u8);
    }

    static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> prefix)
    {
        if (left.Length < prefix.Length)
        {
            return false;
        }

        return EqualsAsciiIgnoreCase(left[..prefix.Length], prefix);
    }

    static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    static byte ToLowerAscii(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(value + 32)
            : value;
    }
}
