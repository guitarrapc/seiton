using System.Globalization;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates <c>workflow_call</c> input default values match their declared types.</summary>
public sealed class WorkflowCallInputDefaultRule() : RuleBase(RuleId.WorkflowCallInputDefault)
{
    public override string Name => "Workflow Call Input Default Rule";

    public override void VisitEvent(Event ev)
    {
        if (ev is not WorkflowCallEvent workflowCall || Config.Utf8Yaml is null || workflowCall.Inputs is null)
        {
            return;
        }

        for (var i = 0; i < workflowCall.Inputs.Count; i++)
        {
            var input = workflowCall.Inputs[i];
            ValidateInputDefault(workflowCall, input);
        }
    }

    private void ValidateInputDefault(WorkflowCallEvent workflowCall, WorkflowCallEventInput input)
    {
        // Check required+default conflict
        if (input.Required.HasValue && Arena.GetBoolValue(input.Required) && input.Default.HasValue)
        {
            var inputName = Decode(Arena.GetStringSlice(input.Name));
            AddEventError(workflowCall, $"workflow_call input '{inputName}' has the default value but is also required. if an input is required, its default value will never be used", Arena.GetStringRange(input.Default));
        }

        if (!input.Default.HasValue || IsExpressionOrInterpolation(input.Default))
        {
            return;
        }

        var defaultValue = Arena.GetStringValue(input.Default);
        switch (input.Type)
        {
            case WorkflowCallInputType.Boolean:
                if (!defaultValue.SequenceEqual("true"u8) && !defaultValue.SequenceEqual("false"u8))
                {
                    var inputName = Decode(Arena.GetStringSlice(input.Name));
                    AddEventError(workflowCall, $"workflow_call input '{inputName}' has boolean type but default is not 'true' or 'false'", Arena.GetStringRange(input.Default));
                }

                break;
            case WorkflowCallInputType.Number:
                if (!double.TryParse(defaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    var inputName = Decode(Arena.GetStringSlice(input.Name));
                    AddEventError(workflowCall, $"workflow_call input '{inputName}' has number type but default is not numeric", Arena.GetStringRange(input.Default));
                }

                break;
        }
    }

    private bool IsExpressionOrInterpolation(StringNodeId node)
    {
        return Arena.GetStringExpression(node).HasValue || Arena.GetStringValue(node).IndexOf("${{"u8) >= 0;
    }
}
