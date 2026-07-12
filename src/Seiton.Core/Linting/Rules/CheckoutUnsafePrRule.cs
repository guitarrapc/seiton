using Seiton.Core.Generated;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags <c>actions/checkout</c> usage with unsafe PR checkout enabled or expression-driven.</summary>
public sealed class CheckoutUnsafePrRule() : RuleBase(RuleId.CheckoutUnsafePr)
{
    private const string InputName = "allow-unsafe-pr-checkout";

    private Utf8Slice _lastUsesSlice;
    private string? _lastMessage;

    public override string Name => "Checkout Unsafe PR Rule";

    public override void VisitStep(StepRef step)
    {
        if (step.Exec.Kind != StepExecKind.Action || Config.Utf8Yaml is null)
        {
            return;
        }

        var actionExec = step.Exec.AsAction();
        if (!actionExec.Inputs.HasValue)
        {
            return;
        }

        var usesText = actionExec.Uses.Value;
        if (!PopularActions.TryGet(usesText, out var actionSpec) || actionSpec.Id != PopularActions.ActionId.ActionsCheckout)
        {
            return;
        }

        if (!actionExec.Inputs.TryGetValue("allow-unsafe-pr-checkout"u8, out var allowUnsafePrCheckoutNode))
        {
            return;
        }

        var value = allowUnsafePrCheckoutNode.Value;
        var containsExpression = ExpressionScanHelpers.ContainsExpressionMarker(allowUnsafePrCheckoutNode.Id, Arena);
        if (!containsExpression && !IsBooleanTrue(value))
        {
            return;
        }

        var message = GetCachedMessage(actionExec.Uses.Slice);
        var location = allowUnsafePrCheckoutNode.Range;
        if (!containsExpression && Config.Fix.Enabled && TryBuildValueReplacementFix(allowUnsafePrCheckoutNode, Config.Utf8Yaml, out var fix))
        {
            AddStepWarning(step, message, location, fix);
            return;
        }

        AddStepWarning(step, message, location);
    }

    private static string BuildMessage(string actionRef)
    {
        return $"action '{actionRef}' should not set with.allow-unsafe-pr-checkout to true or to an expression; this can allow fork pull request code to be checked out in a trusted context and can lead to pwn request vulnerabilities";
    }

    private string GetCachedMessage(Utf8Slice usesSlice)
    {
        if (_lastMessage is not null
            && usesSlice.Length == _lastUsesSlice.Length
            && Config.Utf8Yaml is not null
            && usesSlice.AsSpan(Config.Utf8Yaml).SequenceEqual(_lastUsesSlice.AsSpan(Config.Utf8Yaml)))
        {
            _lastUsesSlice = usesSlice;
            return _lastMessage;
        }

        var actionRef = Decode(usesSlice);
        var msg = BuildMessage(actionRef);
        _lastUsesSlice = usesSlice;
        _lastMessage = msg;
        return msg;
    }

    private bool TryBuildValueReplacementFix(StringRef valueNode, byte[] utf8Yaml, out DiagnosticFix fix)
    {
        fix = default;
        if (ExpressionScanHelpers.ContainsExpressionMarker(valueNode.Id, Arena))
        {
            return false;
        }

        var replacement = BuildReplacementText(valueNode, utf8Yaml);
        fix = new DiagnosticFix(
            $"set with.{InputName} to false",
            [new TextEdit(valueNode.Slice.Offset, valueNode.Slice.Length, replacement)]);
        return true;
    }

    private string BuildReplacementText(StringRef valueNode, byte[] utf8Yaml)
    {
        var valueStart = valueNode.Slice.Offset;
        var valueEnd = valueNode.Slice.Offset + valueNode.Slice.Length;
        if (valueStart < 0 || valueEnd > utf8Yaml.Length || valueStart > valueEnd)
        {
            return "false";
        }

        var valueSpan = valueNode.Value;
        if (valueNode.Quoted)
        {
            if (valueSpan.Length >= 2 && valueSpan[0] == (byte)'\'' && valueSpan[^1] == (byte)'\'')
            {
                return "'false'";
            }

            if (valueSpan.Length >= 2 && valueSpan[0] == (byte)'"' && valueSpan[^1] == (byte)'"')
            {
                return "\"false\"";
            }
        }

        var style = FixFormatting.DetectQuoteStyle(utf8Yaml, valueNode.Range, valueNode.Quoted);
        if (style == ScalarQuoteStyle.Unquoted)
        {
            return "false";
        }

        var quoteChar = style == ScalarQuoteStyle.SingleQuoted ? (byte)'\'' : (byte)'"';
        if (valueStart > 0 && valueEnd < utf8Yaml.Length && utf8Yaml[valueStart - 1] == quoteChar && utf8Yaml[valueEnd] == quoteChar)
        {
            return "false";
        }

        if (valueStart >= 0 && valueEnd - 1 >= valueStart && valueEnd - 1 < utf8Yaml.Length && utf8Yaml[valueStart] == quoteChar && utf8Yaml[valueEnd - 1] == quoteChar)
        {
            return style == ScalarQuoteStyle.SingleQuoted ? "'false'" : "\"false\"";
        }

        return "false";
    }

    private static bool IsBooleanTrue(ReadOnlySpan<byte> value)
    {
        return value.Length == 4
               && (value[0] | 0x20) == (byte)'t'
               && (value[1] | 0x20) == (byte)'r'
               && (value[2] | 0x20) == (byte)'u'
               && (value[3] | 0x20) == (byte)'e';
    }
}
