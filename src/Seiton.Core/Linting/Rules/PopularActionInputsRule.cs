using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class PopularActionInputsRule : RuleBase
{
    public override string Id => "popular-action-inputs";

    public override string Name => "Popular Action Inputs Rule";

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || actionExec.Inputs is null || actionExec.Inputs.Count == 0)
        {
            return;
        }

        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var usesText = actionExec.Uses.Value.AsSpan(Config.Utf8Yaml);
        if (!PopularActions.TryGet(usesText, out var actionSpec))
        {
            return;
        }

        var actionName = Decode(actionExec.Uses.Value);
        foreach (var pair in actionExec.Inputs)
        {
            if (actionSpec.IsInputAllowed(pair.Key.Span))
            {
                continue;
            }

            var inputName = Encoding.UTF8.GetString(pair.Key.Span);
            AddStepWarning(step, $"unknown input '{inputName}' for action '{actionName}'");
        }
    }
}
