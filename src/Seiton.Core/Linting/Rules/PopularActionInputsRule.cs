using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates input names for well-known popular actions against their declared schemas.</summary>
public sealed class PopularActionInputsRule() : RuleBase(RuleId.PopularActionInputs)
{
    public override string Name => "Popular Action Inputs Rule";

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || actionExec.Inputs is null || actionExec.Inputs.Value.Count == 0)
        {
            return;
        }

        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var usesText = Arena.GetStringValue(actionExec.Uses);
        if (!PopularActions.TryGet(usesText, out var actionSpec))
        {
            return;
        }

        var actionName = Decode(Arena.GetStringSlice(actionExec.Uses));
        foreach (var pair in actionExec.Inputs.Value)
        {
            if (actionSpec.IsInputAllowed(pair.Key.AsSpan(Config.Utf8Yaml)))
            {
                continue;
            }

            var inputName = Encoding.UTF8.GetString(pair.Key.AsSpan(Config.Utf8Yaml));
            AddStepWarning(step, $"unknown input '{inputName}' for action '{actionName}'");
        }
    }
}
