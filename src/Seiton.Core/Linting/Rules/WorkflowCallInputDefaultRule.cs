using System.Globalization;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates <c>workflow_call</c> input default values match their declared types.</summary>
public sealed class WorkflowCallInputDefaultRule() : RuleBase(RuleId.WorkflowCallInputDefault)
{
    public override string Name => "Workflow Call Input Default Rule";

    public override void VisitEvent(EventRef ev)
    {
        if (ev.Kind != EventKind.WorkflowCall || Config.Utf8Yaml is null)
        {
            return;
        }

        var workflowCall = ev.AsWorkflowCall();
        if (!workflowCall.Inputs.HasValue)
        {
            return;
        }

        for (var i = 0; i < workflowCall.Inputs.Count; i++)
        {
            var input = workflowCall.Inputs[i];
            ValidateInputDefault(ev, input);
        }
    }

    private void ValidateInputDefault(EventRef workflowCall, WorkflowCallEventInputRef input)
    {
        // Check required+default conflict
        if (input.Required.HasValue && input.Required.Value && input.Default.HasValue)
        {
            var inputName = input.Name.Decode();
            AddEventError(workflowCall, $"workflow_call input '{inputName}' has the default value but is also required. if an input is required, its default value will never be used", input.Default.Range);
        }

        if (!input.Default.HasValue || IsExpressionOrInterpolation(input.Default))
        {
            return;
        }

        var defaultValue = input.Default.Value;
        switch (input.Type)
        {
            case WorkflowCallInputType.Boolean:
                if (!defaultValue.SequenceEqual("true"u8) && !defaultValue.SequenceEqual("false"u8))
                {
                    var inputName = input.Name.Decode();
                    AddEventError(workflowCall, $"workflow_call input '{inputName}' has boolean type but default is not 'true' or 'false'", input.Default.Range);
                }

                break;
            case WorkflowCallInputType.Number:
                if (!double.TryParse(defaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    var inputName = input.Name.Decode();
                    AddEventError(workflowCall, $"workflow_call input '{inputName}' has number type but default is not numeric", input.Default.Range);
                }

                break;
        }
    }

    private bool IsExpressionOrInterpolation(StringRef node)
    {
        return ExpressionScanHelpers.ContainsExpressionMarker(node.Id, Arena);
    }
}
