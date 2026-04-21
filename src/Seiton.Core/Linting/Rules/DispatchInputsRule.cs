using System.Globalization;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

public sealed class DispatchInputsRule : RuleBase
{
    public override string Id => "dispatch-inputs";

    public override string Name => "Dispatch Inputs Rule";

    public override void VisitEvent(Event ev)
    {
        if (ev is not WorkflowDispatchEvent dispatch || Config.Utf8Yaml is null || dispatch.Inputs is null)
        {
            return;
        }

        if (dispatch.Inputs.Count > 25)
        {
            AddEventError(dispatch, "workflow_dispatch event cannot define more than 25 inputs", BuildEventLocation(dispatch));
        }

        foreach (var (_, input) in dispatch.Inputs)
        {
            ValidateInput(dispatch, input);
        }
    }

    private void ValidateInput(WorkflowDispatchEvent dispatch, DispatchInput input)
    {
        var hasOptions = input.Options is not null && input.Options.Count > 0;

        if (input.Type == DispatchInputType.Choice)
        {
            if (!hasOptions)
            {
                var inputName = Decode(input.Name.Value);
                AddEventError(dispatch, $"workflow_dispatch input '{inputName}' of type 'choice' must define non-empty options", input.Name.Range);
                return;
            }

            ValidateChoiceOptionsNoDuplicates(dispatch, input);
            ValidateChoiceDefault(dispatch, input);
            return;
        }

        if (hasOptions)
        {
            var inputName = Decode(input.Name.Value);
            AddEventError(dispatch, $"workflow_dispatch input '{inputName}' has options but type is '{ToTypeText(input.Type)}'; options are only valid for 'choice' type", input.Name.Range);
        }

        if (input.Default is null || IsExpressionOrInterpolation(input.Default))
        {
            return;
        }

        var defaultValue = input.Default.Value.AsSpan(Config.Utf8Yaml);
        switch (input.Type)
        {
            case DispatchInputType.Number:
                if (!double.TryParse(defaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    var inputName = Decode(input.Name.Value);
                    AddEventError(dispatch, $"workflow_dispatch input '{inputName}' has non-numeric default value", input.Default.Range);
                }

                break;
            case DispatchInputType.Boolean:
                if (!defaultValue.SequenceEqual("true"u8) && !defaultValue.SequenceEqual("false"u8))
                {
                    var inputName = Decode(input.Name.Value);
                    AddEventError(dispatch, $"workflow_dispatch input '{inputName}' has boolean default that must be 'true' or 'false'", input.Default.Range);
                }

                break;
        }
    }

    private void ValidateChoiceOptionsNoDuplicates(WorkflowDispatchEvent dispatch, DispatchInput input)
    {
        if (input.Options is null || Config.Utf8Yaml is null)
        {
            return;
        }

        for (var i = 0; i < input.Options.Count; i++)
        {
            var current = input.Options[i];
            if (IsExpressionOrInterpolation(current))
            {
                continue;
            }

            var currentValue = current.Value.AsSpan(Config.Utf8Yaml);
            for (var j = 0; j < i; j++)
            {
                var previous = input.Options[j];
                if (IsExpressionOrInterpolation(previous))
                {
                    continue;
                }

                if (!currentValue.SequenceEqual(previous.Value.AsSpan(Config.Utf8Yaml)))
                {
                    continue;
                }

                var inputName = Decode(input.Name.Value);
                var optionText = Decode(current.Value);
                AddEventError(dispatch, $"workflow_dispatch input '{inputName}' has duplicated option '{optionText}'", current.Range);
                break;
            }
        }
    }

    private void ValidateChoiceDefault(WorkflowDispatchEvent dispatch, DispatchInput input)
    {
        if (input.Options is null || input.Default is null || IsExpressionOrInterpolation(input.Default) || Config.Utf8Yaml is null)
        {
            return;
        }

        var defaultValue = input.Default.Value.AsSpan(Config.Utf8Yaml);
        for (var i = 0; i < input.Options.Count; i++)
        {
            var optionNode = input.Options[i];
            if (IsExpressionOrInterpolation(optionNode))
            {
                return;
            }

            if (optionNode.Value.AsSpan(Config.Utf8Yaml).SequenceEqual(defaultValue))
            {
                return;
            }
        }

        var inputName = Decode(input.Name.Value);
        var defaultText = Decode(input.Default.Value);
        AddEventError(dispatch, $"workflow_dispatch input '{inputName}' has default value '{defaultText}' which is not included in options", input.Default.Range);
    }

    private static string ToTypeText(DispatchInputType type)
    {
        return type switch
        {
            DispatchInputType.String => "string",
            DispatchInputType.Number => "number",
            DispatchInputType.Boolean => "boolean",
            DispatchInputType.Choice => "choice",
            DispatchInputType.Environment => "environment",
            _ => "none",
        };
    }

    private bool IsExpressionOrInterpolation(StringNode node)
    {
        return node.Expression is not null || node.Value.AsSpan(Config.Utf8Yaml).IndexOf("${{"u8) >= 0;
    }
}
