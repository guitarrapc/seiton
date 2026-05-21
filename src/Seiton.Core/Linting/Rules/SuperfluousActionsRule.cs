using System.Runtime.CompilerServices;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Suggests built-in runner tooling instead of thin wrapper actions.</summary>
public sealed class SuperfluousActionsRule() : RuleBase(RuleId.SuperfluousActions)
{
    public override string Name => "Superfluous Actions Rule";

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecAction actionExec)
        {
            return;
        }

        if (!TryGetReplacement(Arena.GetStringValue(actionExec.Uses), out var actionName, out var replacement))
        {
            return;
        }

        AddStepInfo(step, $"action '{actionName}' can often be replaced with built-in tooling such as '{replacement}'", BuildUsesLocation(actionExec));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetReplacement(ReadOnlySpan<byte> uses, out string actionName, out string replacement)
    {
        actionName = string.Empty;
        replacement = string.Empty;
        if (!ActionRefHelpers.TryParseRemoteUses(uses, out var parsed))
        {
            return false;
        }

        Span<byte> ownerRepoScratch = stackalloc byte[64];
        if (!ActionRefHelpers.TryGetOwnerRepoPolicyKey(parsed.ActionPath, ownerRepoScratch, out var ownerRepo))
        {
            return false;
        }

        if (ownerRepo.SequenceEqual("ncipollo/release-action"u8))
        {
            actionName = "ncipollo/release-action";
            replacement = "gh release create";
            return true;
        }

        if (ownerRepo.SequenceEqual("softprops/action-gh-release"u8))
        {
            actionName = "softprops/action-gh-release";
            replacement = "gh release create";
            return true;
        }

        if (ownerRepo.SequenceEqual("elgohr/github-release-action"u8))
        {
            actionName = "elgohr/Github-Release-Action";
            replacement = "gh release create";
            return true;
        }

        if (ownerRepo.SequenceEqual("dacbd/create-issue-action"u8))
        {
            actionName = "dacbd/create-issue-action";
            replacement = "gh issue create";
            return true;
        }

        if (ownerRepo.SequenceEqual("actions-ecosystem/action-add-labels"u8))
        {
            actionName = "actions-ecosystem/action-add-labels";
            replacement = "gh issue edit --add-label";
            return true;
        }

        if (ownerRepo.SequenceEqual("actions-ecosystem/action-remove-labels"u8))
        {
            actionName = "actions-ecosystem/action-remove-labels";
            replacement = "gh issue edit --remove-label";
            return true;
        }

        if (ownerRepo.SequenceEqual("svenstaro/upload-release-action"u8))
        {
            actionName = "svenstaro/upload-release-action";
            replacement = "gh release create";
            return true;
        }

        if (ownerRepo.SequenceEqual("addnab/docker-run-action"u8))
        {
            actionName = "addnab/docker-run-action";
            replacement = "docker run";
            return true;
        }

        if (ownerRepo.SequenceEqual("sergeysova/jq-action"u8))
        {
            actionName = "sergeysova/jq-action";
            replacement = "jq";
            return true;
        }

        return false;
    }
}
