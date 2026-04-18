using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class ActionShellIsRequiredRule : RuleBase
{
    public override string Id => "action-shell-is-required";

    public override string Name => "Action Shell Is Required Rule";

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecRun run)
        {
            return;
        }

        if (run.Shell is not null && !IsMissingShell(run.Shell.Value.AsSpan(Config.Utf8Yaml)))
        {
            return;
        }

        AddStepError(step, "shell is required if run is set", run.Run.Range);
    }

    static bool IsMissingShell(ReadOnlySpan<byte> value)
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
