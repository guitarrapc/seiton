using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags use of deprecated workflow commands (e.g. <c>::set-output</c>, <c>::set-env</c>) in run scripts.</summary>
public sealed class DeprecatedCommandsRule() : RuleBase(RuleId.DeprecatedCommands)
{
    public override string Name => "Deprecated Commands Rule";

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecRun run)
        {
            return;
        }

        var script = Arena.GetStringValue(run.Run);

        if (ContainsAsciiIgnoreCase(script, "::set-output"u8))
        {
            AddStepWarning(step, "run script uses deprecated command '::set-output'; use $GITHUB_OUTPUT instead", Arena.GetStringRange(run.Run));
        }

        if (ContainsAsciiIgnoreCase(script, "::save-state"u8))
        {
            AddStepWarning(step, "run script uses deprecated command '::save-state'; use $GITHUB_STATE instead", Arena.GetStringRange(run.Run));
        }

        if (ContainsAsciiIgnoreCase(script, "::add-path"u8))
        {
            AddStepWarning(step, "run script uses deprecated command '::add-path'; use $GITHUB_PATH instead", Arena.GetStringRange(run.Run));
        }

        if (ContainsAsciiIgnoreCase(script, "::set-env"u8))
        {
            AddStepWarning(step, "run script uses deprecated command '::set-env'; use $GITHUB_ENV instead", Arena.GetStringRange(run.Run));
        }
    }
}
