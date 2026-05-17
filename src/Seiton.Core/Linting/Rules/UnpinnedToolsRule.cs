using System.Runtime.CompilerServices;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>
/// Flags steps that use known setup/tool actions without pinning the tool version,
/// or using <c>version: latest</c>, or using a dynamic expression for the version.
/// </summary>
public sealed class UnpinnedToolsRule() : RuleBase(RuleId.UnpinnedTools)
{
    // Known actions that install external tools where version pinning matters.
    // Format: "owner/repo" (case-insensitive matching against uses before @ref).
    private static ReadOnlySpan<byte> SetupTrivy => "aquasecurity/setup-trivy"u8;
    private static ReadOnlySpan<byte> LoadSecretsAction => "1password/load-secrets-action"u8;

    private static ReadOnlySpan<byte> VersionKey => "version"u8;
    private static ReadOnlySpan<byte> LatestValue => "latest"u8;

    private const string SetupTrivyMissingVersionMessage = "'aquasecurity/setup-trivy' does not specify 'version' input; implicitly uses unpinned latest version";
    private const string SetupTrivyLatestMessage = "'aquasecurity/setup-trivy' specifies 'version: latest' which is unpinned; pin to a specific version";
    private const string SetupTrivyDynamicMessage = "'aquasecurity/setup-trivy' specifies 'version' dynamically which may be unpinned";
    private const string LoadSecretsMissingVersionMessage = "'1password/load-secrets-action' does not specify 'version' input; implicitly uses unpinned latest version";
    private const string LoadSecretsLatestMessage = "'1password/load-secrets-action' specifies 'version: latest' which is unpinned; pin to a specific version";
    private const string LoadSecretsDynamicMessage = "'1password/load-secrets-action' specifies 'version' dynamically which may be unpinned";

    public override string Name => "Unpinned Tools Rule";

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = Arena.GetStringValue(actionExec.Uses);
        if (uses.Length == 0)
        {
            return;
        }

        if (!TryGetKnownAction(uses, out var knownAction))
        {
            return;
        }

        // Check if version input is provided
        if (actionExec.Inputs is null || !actionExec.Inputs.Value.TryGetValue(Config.Utf8Yaml, VersionKey, out var versionNode))
        {
            var location = BuildUsesLocation(actionExec);
            AddStepWarning(step, knownAction.MissingVersionMessage, location);
            return;
        }

        // Check the version value
        var versionValue = Arena.GetStringValue(versionNode);
        if (versionValue.Length == 0)
        {
            return;
        }

        // version: latest
        if (versionValue.SequenceEqual(LatestValue))
        {
            var location = Arena.GetStringRange(versionNode);
            AddStepWarning(step, knownAction.LatestMessage, location);
            return;
        }

        // version: ${{ expr }} - dynamic expression may be unpinned
        if (ExpressionScanHelpers.TryExtractExpressionBody(versionValue, out _))
        {
            var location = Arena.GetStringRange(versionNode);
            AddStepWarning(step, knownAction.DynamicMessage, location);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetKnownAction(ReadOnlySpan<byte> uses, out KnownAction knownAction)
    {
        knownAction = default;
        if (!ActionRefHelpers.TryParseRemoteUses(uses, out var parsed))
        {
            return false;
        }

        Span<byte> ownerRepoScratch = stackalloc byte[96];
        if (!ActionRefHelpers.TryGetOwnerRepoPolicyKey(parsed.ActionPath, ownerRepoScratch, out var ownerRepo))
        {
            return false;
        }

        if (ownerRepo.SequenceEqual(SetupTrivy))
        {
            knownAction = new KnownAction(SetupTrivyMissingVersionMessage, SetupTrivyLatestMessage, SetupTrivyDynamicMessage);
            return true;
        }

        if (ownerRepo.SequenceEqual(LoadSecretsAction))
        {
            knownAction = new KnownAction(LoadSecretsMissingVersionMessage, LoadSecretsLatestMessage, LoadSecretsDynamicMessage);
            return true;
        }

        return false;
    }

    private readonly record struct KnownAction(string MissingVersionMessage, string LatestMessage, string DynamicMessage);
}
