using System.Globalization;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates <c>workflow_dispatch</c> input definitions for structural correctness.</summary>
public sealed class DispatchInputsRule() : RuleBase(RuleId.DispatchInputs)
{
    public override string Name => "Dispatch Inputs Rule";

    public override void VisitEvent(Event ev)
    {
        if (ev is not WorkflowDispatchEvent dispatch || Config.Utf8Yaml is null || dispatch.Inputs is null)
        {
            return;
        }

        if (dispatch.Inputs.Value.Count > 25)
        {
            AddEventError(dispatch, $"maximum number of inputs for \"workflow_dispatch\" event is 25 but {dispatch.Inputs.Value.Count} inputs are provided", BuildEventLocation(dispatch));
        }

        foreach (var (_, input) in dispatch.Inputs.Value)
        {
            ValidateInput(dispatch, input);
        }
    }

    private void ValidateInput(WorkflowDispatchEvent dispatch, DispatchInput input)
    {
        var hasOptions = input.Options is not null && input.Options.Length > 0;

        if (input.Type == DispatchInputType.Choice)
        {
            if (!hasOptions)
            {
                var inputName = Decode(Arena.GetStringSlice(input.Name));
                AddEventError(dispatch, $"workflow_dispatch input '{inputName}' of type 'choice' must define non-empty options", Arena.GetStringRange(input.Name));
                return;
            }

            ValidateChoiceOptionsNoDuplicates(dispatch, input);
            ValidateChoiceDefault(dispatch, input);
            return;
        }

        if (hasOptions)
        {
            var inputName = Decode(Arena.GetStringSlice(input.Name));
            AddEventError(dispatch, $"workflow_dispatch input '{inputName}' has options but type is '{ToTypeText(input.Type)}'; options are only valid for 'choice' type", Arena.GetStringRange(input.Name));
        }

        if (!input.Default.HasValue || IsExpressionOrInterpolation(input.Default))
        {
            return;
        }

        var defaultValue = Arena.GetStringValue(input.Default);
        switch (input.Type)
        {
            case DispatchInputType.Number:
                if (!double.TryParse(defaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    var inputName = Decode(Arena.GetStringSlice(input.Name));
                    var defaultText = Decode(Arena.GetStringSlice(input.Default));
                    AddEventError(dispatch, $"workflow_dispatch input '{inputName}' default value '{defaultText}' is not a valid number", Arena.GetStringRange(input.Default));
                }

                break;
            case DispatchInputType.Boolean:
                if (!defaultValue.SequenceEqual("true"u8) && !defaultValue.SequenceEqual("false"u8))
                {
                    var inputName = Decode(Arena.GetStringSlice(input.Name));
                    var defaultText = Decode(Arena.GetStringSlice(input.Default));
                    AddEventError(dispatch, $"workflow_dispatch input '{inputName}' boolean default value '{defaultText}' must be 'true' or 'false'", Arena.GetStringRange(input.Default));
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

        for (var i = 0; i < input.Options.Length; i++)
        {
            var current = input.Options[i];
            if (IsExpressionOrInterpolation(current))
            {
                continue;
            }

            var currentValue = Arena.GetStringValue(current);
            for (var j = 0; j < i; j++)
            {
                var previous = input.Options[j];
                if (IsExpressionOrInterpolation(previous))
                {
                    continue;
                }

                if (!currentValue.SequenceEqual(Arena.GetStringValue(previous)))
                {
                    continue;
                }

                var inputName = Decode(Arena.GetStringSlice(input.Name));
                var optionText = Decode(Arena.GetStringSlice(current));
                AddEventError(dispatch, $"workflow_dispatch input '{inputName}' has duplicated option '{optionText}'", Arena.GetStringRange(current));
                break;
            }
        }
    }

    private void ValidateChoiceDefault(WorkflowDispatchEvent dispatch, DispatchInput input)
    {
        if (input.Options is null || !input.Default.HasValue || IsExpressionOrInterpolation(input.Default) || Config.Utf8Yaml is null)
        {
            return;
        }

        var defaultValue = Arena.GetStringValue(input.Default);
        for (var i = 0; i < input.Options.Length; i++)
        {
            var optionNode = input.Options[i];
            if (IsExpressionOrInterpolation(optionNode))
            {
                return;
            }

            if (Arena.GetStringValue(optionNode).SequenceEqual(defaultValue))
            {
                return;
            }
        }

        var inputName = Decode(Arena.GetStringSlice(input.Name));
        var defaultText = Decode(Arena.GetStringSlice(input.Default));
        AddEventError(dispatch, $"workflow_dispatch input '{inputName}' has default value '{defaultText}' which is not included in options", Arena.GetStringRange(input.Default));
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

    private bool IsExpressionOrInterpolation(StringNodeId node)
    {
        return ExpressionScanHelpers.ContainsExpressionMarker(node, Arena);
    }
}
