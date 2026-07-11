using System.Runtime.CompilerServices;
using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>
/// Flags steps that use known setup/tool actions without pinning the tool version,
/// or using <c>version: latest</c>, or using a dynamic expression for the version.
/// Known actions are maintained in <c>data/sources/unpinned-tools/unpinned_tools.json</c>
/// and code-generated into <see cref="UnpinnedToolsActions"/>.
/// </summary>
public sealed class UnpinnedToolsRule() : RuleBase(RuleId.UnpinnedTools)
{
    private static ReadOnlySpan<byte> LatestValue => "latest"u8;

    public override string Name => "Unpinned Tools Rule";

    public override void VisitStep(StepRef step)
    {
        if (step.Exec.Kind != StepExecKind.Action || Config.Utf8Yaml is null)
        {
            return;
        }

        var actionExec = step.Exec.AsAction();
        var uses = actionExec.Uses.Value;
        if (uses.Length == 0)
        {
            return;
        }

        if (!TryGetKnownAction(uses, out var actionIndex))
        {
            return;
        }

        var versionKey = UnpinnedToolsActions.GetVersionInputKey(actionIndex);

        // Check if version input is provided
        if (!actionExec.Inputs.TryGetValue(versionKey, out var versionNode))
        {
            var location = BuildUsesLocation(actionExec);
            AddStepWarning(step, UnpinnedToolsActions.GetMissingVersionMessage(actionIndex), location);
            return;
        }

        // Check the version value
        var versionValue = versionNode.Value;
        if (versionValue.Length == 0)
        {
            return;
        }

        // version: latest
        if (versionValue.SequenceEqual(LatestValue))
        {
            var location = versionNode.Range;
            AddStepWarning(step, UnpinnedToolsActions.GetLatestMessage(actionIndex), location);
            return;
        }

        // version: ${{ expr }} - dynamic expression may be unpinned
        if (ExpressionScanHelpers.TryExtractExpressionBody(versionValue, out _))
        {
            var location = versionNode.Range;
            AddStepWarning(step, UnpinnedToolsActions.GetDynamicMessage(actionIndex), location);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetKnownAction(ReadOnlySpan<byte> uses, out int actionIndex)
    {
        actionIndex = -1;
        if (!ActionRefHelpers.TryParseRemoteUses(uses, out var parsed))
        {
            return false;
        }

        Span<byte> ownerRepoScratch = stackalloc byte[96];
        if (!ActionRefHelpers.TryGetOwnerRepoPolicyKey(parsed.ActionPath, ownerRepoScratch, out var ownerRepo))
        {
            return false;
        }

        return UnpinnedToolsActions.TryGetKnownActionIndex(ownerRepo, out actionIndex);
    }
}
