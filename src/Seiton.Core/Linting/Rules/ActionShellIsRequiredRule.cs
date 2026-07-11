using Seiton.Core.Parsing.Ast;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.Rules;

/// <summary>Requires composite action <c>run:</c> steps to explicitly declare <c>shell:</c>.</summary>
public sealed class ActionShellIsRequiredRule() : RuleBase(RuleId.ActionShellIsRequired)
{
    public override string Name => "Action Shell Is Required Rule";

    public override bool SupportsDocumentKind(DocumentKind documentKind)
    {
        return documentKind == DocumentKind.ActionMetadata;
    }

    public override void VisitStep(StepRef step)
    {
        if (Config.Utf8Yaml is null || step.Exec.Kind != StepExecKind.Run)
        {
            return;
        }

        var run = step.Exec.AsRun();
        if (run.Shell.HasValue && !IsMissingShell(run.Shell.Value))
        {
            return;
        }

        AddStepError(step, "shell is required if run is set", run.Run.Range);
    }

    private static bool IsMissingShell(ReadOnlySpan<byte> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != (byte)' ' && value[i] != (byte)'\t' && value[i] != (byte)'\n' && value[i] != (byte)'\r')
            {
                return false;
            }
        }

        return true;
    }
}
