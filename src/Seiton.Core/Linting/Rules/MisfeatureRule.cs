using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags known GitHub Actions features that are supported but discouraged.</summary>
public sealed class MisfeatureRule() : RuleBase(RuleId.Misfeature)
{
    private static ReadOnlySpan<byte> SetupPython => "actions/setup-python"u8;
    private static ReadOnlySpan<byte> PipInstallKey => "pip-install"u8;

    public override string Name => "Misfeature Rule";

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecAction actionExec)
        {
            return;
        }

        if (!ActionRefHelpers.TryParseRemoteUses(Arena.GetStringValue(actionExec.Uses), out var parsed))
        {
            return;
        }

        Span<byte> ownerRepoScratch = stackalloc byte[32];
        if (!ActionRefHelpers.TryGetOwnerRepoPolicyKey(parsed.ActionPath, ownerRepoScratch, out var ownerRepo)
            || !ownerRepo.SequenceEqual(SetupPython))
        {
            return;
        }

        if (actionExec.Inputs is null || !actionExec.Inputs.Value.TryGetValue(Config.Utf8Yaml, PipInstallKey, out var pipInstallValue))
        {
            return;
        }

        AddStepInfo(step, "actions/setup-python with 'pip-install' is discouraged; prefer installing dependencies in an explicit run step or virtual environment", GetRange(pipInstallValue));
    }
}
