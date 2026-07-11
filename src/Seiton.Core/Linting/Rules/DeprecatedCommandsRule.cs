using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags use of deprecated workflow commands (e.g. <c>::set-output</c>, <c>::set-env</c>) in run scripts.</summary>
public sealed class DeprecatedCommandsRule() : RuleBase(RuleId.DeprecatedCommands)
{
    public override string Name => "Deprecated Commands Rule";

    private const string DocsUrl = "https://docs.github.com/en/actions/using-workflows/workflow-commands-for-github-actions";

    public override void VisitStep(StepRef step)
    {
        if (Config.Utf8Yaml is null || step.Exec.Kind != StepExecKind.Run)
        {
            return;
        }

        var run = step.Exec.AsRun();
        var script = run.Run.Value;

        if (ContainsAsciiIgnoreCase(script, "::set-output"u8))
        {
            AddStepWarning(step, $"workflow command \"set-output\" was deprecated. use `echo \"{{name}}={{value}}\" >> $GITHUB_OUTPUT` instead: {DocsUrl}", run.Run.Range);
        }

        if (ContainsAsciiIgnoreCase(script, "::save-state"u8))
        {
            AddStepWarning(step, $"workflow command \"save-state\" was deprecated. use `echo \"{{name}}={{value}}\" >> $GITHUB_STATE` instead: {DocsUrl}", run.Run.Range);
        }

        if (ContainsAsciiIgnoreCase(script, "::add-path"u8))
        {
            AddStepWarning(step, $"workflow command \"add-path\" was deprecated. use `echo \"{{path}}\" >> $GITHUB_PATH` instead: {DocsUrl}", run.Run.Range);
        }

        if (ContainsAsciiIgnoreCase(script, "::set-env"u8))
        {
            AddStepWarning(step, $"workflow command \"set-env\" was deprecated. use `echo \"{{name}}={{value}}\" >> $GITHUB_ENV` instead: {DocsUrl}", run.Run.Range);
        }
    }
}
