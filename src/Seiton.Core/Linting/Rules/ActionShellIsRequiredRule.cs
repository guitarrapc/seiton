using Seiton.Core.Parsing.Ast;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.Rules;

public sealed class ActionShellIsRequiredRule() : RuleBase(RuleId.ActionShellIsRequired)
{
    public override string Name => "Action Shell Is Required Rule";

    public override bool SupportsDocumentKind(DocumentKind documentKind)
    {
        return documentKind == DocumentKind.ActionMetadata;
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecRun run)
        {
            return;
        }

        if (run.Shell.HasValue && !IsMissingShell(Arena.GetStringValue(run.Shell)))
        {
            return;
        }

        AddStepError(step, "shell is required if run is set", Arena.GetStringRange(run.Run));
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
