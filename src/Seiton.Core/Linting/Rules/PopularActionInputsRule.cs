using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates input names for well-known popular actions against their declared schemas.</summary>
public sealed class PopularActionInputsRule() : RuleBase(RuleId.PopularActionInputs)
{
    public override string Name => "Popular Action Inputs Rule";

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec)
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

        // Check unknown inputs
        if (actionExec.Inputs is { Count: > 0 } inputs)
        {
            foreach (var pair in inputs)
            {
                if (actionSpec.IsInputAllowed(pair.Key.AsSpan(Config.Utf8Yaml)))
                {
                    // Check deprecated inputs
                    var deprecationMessage = actionSpec.GetDeprecatedInputMessage(pair.Key.AsSpan(Config.Utf8Yaml));
                    if (!deprecationMessage.IsEmpty)
                    {
                        var inputName = Encoding.UTF8.GetString(pair.Key.AsSpan(Config.Utf8Yaml));
                        var message = Encoding.UTF8.GetString(deprecationMessage);
                        AddStepWarning(step, $"avoid using deprecated input \"{inputName}\" in action \"{actionName}\": {message}");
                    }

                    continue;
                }

                var unknownInputName = Encoding.UTF8.GetString(pair.Key.AsSpan(Config.Utf8Yaml));
                AddStepWarning(step, $"unknown input '{unknownInputName}' for action '{actionName}'");
            }
        }

        // Check missing required inputs
        var requiredInputs = actionSpec.GetRequiredInputs();
        for (var i = 0; i < requiredInputs.Length; i++)
        {
            var requiredUtf8 = requiredInputs[i].AsSpan();
            if (IsInputProvided(actionExec, requiredUtf8))
            {
                continue;
            }

            var requiredName = Encoding.UTF8.GetString(requiredUtf8);
            AddStepWarning(step, $"missing required input '{requiredName}' for action '{actionName}'");
        }
    }

    private bool IsInputProvided(ExecAction actionExec, ReadOnlySpan<byte> inputNameUtf8)
    {
        if (actionExec.Inputs is not { Count: > 0 } inputs || Config.Utf8Yaml is null)
        {
            return false;
        }

        foreach (var pair in inputs)
        {
            if (SpanHelpers.EqualsAsciiIgnoreCase(pair.Key.AsSpan(Config.Utf8Yaml), inputNameUtf8))
            {
                return true;
            }
        }

        return false;
    }
}
