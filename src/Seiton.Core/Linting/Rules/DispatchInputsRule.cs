using System.Globalization;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates <c>workflow_dispatch</c> input definitions for structural correctness.</summary>
public sealed class DispatchInputsRule() : RuleBase(RuleId.DispatchInputs)
{
    public override string Name => "Dispatch Inputs Rule";

    public override void VisitEvent(EventRef ev)
    {
        if (ev.Kind != EventKind.WorkflowDispatch || Config.Utf8Yaml is null)
        {
            return;
        }

        var dispatch = ev.AsWorkflowDispatch();
        if (!dispatch.Inputs.HasValue)
        {
            return;
        }

        if (dispatch.Inputs.Count > 25)
        {
            AddEventError(ev, $"maximum number of inputs for \"workflow_dispatch\" event is 25 but {dispatch.Inputs.Count} inputs are provided", BuildEventLocation(ev));
        }

        foreach (var (_, input) in dispatch.Inputs)
        {
            ValidateInput(ev, input);
        }
    }

    private void ValidateInput(EventRef dispatch, DispatchInputRef input)
    {
        var hasOptions = input.Options.HasValue && input.Options.Count > 0;

        if (input.Type == DispatchInputType.Choice)
        {
            if (!hasOptions)
            {
                var inputName = input.Name.Decode();
                AddEventError(dispatch, $"workflow_dispatch input '{inputName}' of type 'choice' must define non-empty options", input.Name.Range);
                return;
            }

            ValidateChoiceOptionsNoDuplicates(dispatch, input);
            ValidateChoiceDefault(dispatch, input);
            return;
        }

        if (hasOptions)
        {
            var inputName = input.Name.Decode();
            AddEventError(dispatch, $"workflow_dispatch input '{inputName}' has options but type is '{ToTypeText(input.Type)}'; options are only valid for 'choice' type", input.Name.Range);
        }

        if (!input.Default.HasValue || IsExpressionOrInterpolation(input.Default))
        {
            return;
        }

        var defaultValue = input.Default.Value;
        switch (input.Type)
        {
            case DispatchInputType.Number:
                if (!double.TryParse(defaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    var inputName = input.Name.Decode();
                    var defaultText = input.Default.Decode();
                    AddEventError(dispatch, $"workflow_dispatch input '{inputName}' default value '{defaultText}' is not a valid number", input.Default.Range);
                }

                break;
            case DispatchInputType.Boolean:
                if (!defaultValue.SequenceEqual("true"u8) && !defaultValue.SequenceEqual("false"u8))
                {
                    var inputName = input.Name.Decode();
                    var defaultText = input.Default.Decode();
                    AddEventError(dispatch, $"workflow_dispatch input '{inputName}' boolean default value '{defaultText}' must be 'true' or 'false'", input.Default.Range);
                }

                break;
        }
    }

    private void ValidateChoiceOptionsNoDuplicates(EventRef dispatch, DispatchInputRef input)
    {
        if (!input.Options.HasValue || Config.Utf8Yaml is null)
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

            var currentValue = current.Value;
            for (var j = 0; j < i; j++)
            {
                var previous = input.Options[j];
                if (IsExpressionOrInterpolation(previous))
                {
                    continue;
                }

                if (!currentValue.SequenceEqual(previous.Value))
                {
                    continue;
                }

                var inputName = input.Name.Decode();
                var optionText = current.Decode();
                AddEventError(dispatch, $"workflow_dispatch input '{inputName}' has duplicated option '{optionText}'", current.Range);
                break;
            }
        }
    }

    private void ValidateChoiceDefault(EventRef dispatch, DispatchInputRef input)
    {
        if (!input.Options.HasValue || !input.Default.HasValue || IsExpressionOrInterpolation(input.Default) || Config.Utf8Yaml is null)
        {
            return;
        }

        var defaultValue = input.Default.Value;
        for (var i = 0; i < input.Options.Count; i++)
        {
            var optionNode = input.Options[i];
            if (IsExpressionOrInterpolation(optionNode))
            {
                return;
            }

            if (optionNode.Value.SequenceEqual(defaultValue))
            {
                return;
            }
        }

        var inputName = input.Name.Decode();
        var defaultText = input.Default.Decode();
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

    private bool IsExpressionOrInterpolation(StringRef node)
    {
        return ExpressionScanHelpers.ContainsExpressionMarker(node.Id, Arena);
    }
}
