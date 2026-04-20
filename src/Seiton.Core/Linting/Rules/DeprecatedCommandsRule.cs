using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class DeprecatedCommandsRule : RuleBase
{
    public override string Id => "deprecated-commands";

    public override string Name => "Deprecated Commands Rule";

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecRun run)
        {
            return;
        }

        var script = run.Run.Value.AsSpan(Config.Utf8Yaml);

        if (ContainsAsciiIgnoreCase(script, "::set-output"u8))
        {
            AddStepWarning(step, "run script uses deprecated command '::set-output'; use $GITHUB_OUTPUT instead", run.Run.Range);
            return;
        }

        if (ContainsAsciiIgnoreCase(script, "::save-state"u8))
        {
            AddStepWarning(step, "run script uses deprecated command '::save-state'; use $GITHUB_STATE instead", run.Run.Range);
            return;
        }

        if (ContainsAsciiIgnoreCase(script, "::add-path"u8))
        {
            AddStepWarning(step, "run script uses deprecated command '::add-path'; use $GITHUB_PATH instead", run.Run.Range);
            return;
        }

        if (ContainsAsciiIgnoreCase(script, "::set-env"u8))
        {
            AddStepWarning(step, "run script uses deprecated command '::set-env'; use $GITHUB_ENV instead", run.Run.Range);
        }
    }
}
