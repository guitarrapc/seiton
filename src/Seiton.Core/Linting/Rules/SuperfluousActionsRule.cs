using System.Runtime.CompilerServices;
using Seiton.Core.Generated;
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

        return SuperfluousActions.TryGetReplacement(ownerRepo, out actionName, out replacement);
    }
}
