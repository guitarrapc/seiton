using Seiton.Core.Generated;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags <c>actions/checkout</c> usage without <c>persist-credentials: false</c>.</summary>
public sealed class CheckoutPersistCredentialsRule() : RuleBase(RuleId.CheckoutPersistCredentials)
{
    private const string PersistCredentialsKey = "persist-credentials";
    private const string FixHint = "review later authenticated git commands; for example, git push may require explicit auth setup such as `git remote set-url origin <url>` or `gh auth setup-git`";

    // Cache last-produced message to avoid repeated string allocation for the same action ref
    private Utf8Slice _lastUsesSlice;
    private string? _lastMessage;

    public override string Name => "Checkout Persist Credentials Rule";

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || Config.Utf8Yaml is null)
        {
            return;
        }

        var usesText = Arena.GetStringValue(actionExec.Uses);
        if (!PopularActions.TryGet(usesText, out var actionSpec) || actionSpec.Id != PopularActions.ActionId.ActionsCheckout)
        {
            return;
        }

        var usesSlice = Arena.GetStringSlice(actionExec.Uses);
        var message = GetCachedMessage(usesSlice);

        if (actionExec.Inputs is null || Config.Utf8Yaml is null || !actionExec.Inputs.Value.TryGetValue(Config.Utf8Yaml, "persist-credentials"u8, out var persistCredentialsNode))
        {
            if (Config.Fix.Enabled && Config.Utf8Yaml is not null && TryBuildMissingInputFix(step, actionExec, Config.Utf8Yaml, out var missingFix))
            {
                AddStepWarning(step, message, BuildStepLocation(step), missingFix);
                return;
            }

            AddStepWarning(step, message);
            return;
        }

        var value = Arena.GetStringValue(persistCredentialsNode);
        if (!ExpressionScanHelpers.ContainsExpressionMarker(persistCredentialsNode, Arena) && IsBooleanFalse(value))
        {
            return;
        }

        if (Config.Fix.Enabled && Config.Utf8Yaml is not null && TryBuildValueReplacementFix(persistCredentialsNode, Config.Utf8Yaml, out var valueFix))
        {
            AddStepWarning(step, message, Arena.GetStringRange(persistCredentialsNode), valueFix);
            return;
        }

        AddStepWarning(step, message, Arena.GetStringRange(persistCredentialsNode));
    }

    private static string BuildMessage(string actionRef)
    {
        return $"action '{actionRef}' should set with.persist-credentials to false to avoid leaving credentials accessible to subsequent steps; after changing this, {FixHint}";
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

    private bool TryBuildValueReplacementFix(StringNodeId persistCredentialsNode, byte[] utf8Yaml, out DiagnosticFix fix)
    {
        fix = default;
        if (ExpressionScanHelpers.ContainsExpressionMarker(persistCredentialsNode, Arena))
        {
            return false;
        }

        var replacement = BuildReplacementText(persistCredentialsNode, utf8Yaml);
        fix = new DiagnosticFix(
            $"set with.{PersistCredentialsKey} to false; {FixHint}",
            [new TextEdit(Arena.GetStringSlice(persistCredentialsNode).Offset, Arena.GetStringSlice(persistCredentialsNode).Length, replacement)]);
        return true;
    }

    private bool TryBuildMissingInputFix(Step step, ExecAction actionExec, byte[] utf8Yaml, out DiagnosticFix fix)
    {
        fix = default;

        if (utf8Yaml.Length == 0)
        {
            return false;
        }

        var usesLine = Utf8YamlLineHelpers.FindLineNumberFromOffset(utf8Yaml, Arena.GetStringSlice(actionExec.Uses).Offset);
        if (usesLine < 1)
        {
            return false;
        }

        // step.Range.EndLine reflects only the 'uses' value's position, not the whole step extent.
        // Use usesLine + 1 as the minimum so the search always covers at least one line past 'uses'.
        var stepEndLine = step.Range.EndLine > 0
            ? Math.Max(usesLine + 1, step.Range.EndLine)
            : int.MaxValue;
        var lineEnding = FixFormatting.DetectDominantLineEnding(utf8Yaml);
        var keyIndent = GetStepKeyIndentation(utf8Yaml, usesLine);

        if (actionExec.Inputs is not null && actionExec.Inputs.Value.Count > 0)
        {
            var withLine = Utf8YamlLineHelpers.FindLineWithKey(utf8Yaml, Math.Max(usesLine + 1, 1), stepEndLine, keyIndent, "with:"u8);
            if (withLine < 0 || LineContainsFlowMappingAt(utf8Yaml, withLine, keyIndent))
            {
                return false;
            }

            var firstInputLine = FindFirstInputLine(utf8Yaml, actionExec.Inputs.Value);
            if (firstInputLine < 1)
            {
                return false;
            }

            var inputIndent = FixFormatting.GetLineIndentation(utf8Yaml, firstInputLine);
            var insertOffset = Utf8YamlLineHelpers.FindLineStartOffset(utf8Yaml, firstInputLine);
            var insertText = inputIndent + PersistCredentialsKey + ": false" + lineEnding;

            fix = new DiagnosticFix(
                $"insert with.{PersistCredentialsKey}: false; {FixHint}",
                [new TextEdit(insertOffset, 0, insertText)]);
            return true;
        }

        var withIndent = keyIndent;
        var childIndent = withIndent + FixFormatting.InferIndentationUnit(utf8Yaml);
        var insertAfterUsesOffset = Utf8YamlLineHelpers.FindLineEndOffsetIncludingNewLine(utf8Yaml, usesLine);
        var withBlock = withIndent + "with:" + lineEnding + childIndent + PersistCredentialsKey + ": false" + lineEnding;
        var insertTextNoWith = insertAfterUsesOffset > 0 && insertAfterUsesOffset <= utf8Yaml.Length && utf8Yaml[insertAfterUsesOffset - 1] != (byte)'\n'
            ? lineEnding + withBlock
            : withBlock;

        fix = new DiagnosticFix(
            $"insert with.{PersistCredentialsKey}: false; {FixHint}",
            [new TextEdit(insertAfterUsesOffset, 0, insertTextNoWith)]);
        return true;
    }

    private string BuildReplacementText(StringNodeId valueNode, byte[] utf8Yaml)
    {
        var valueStart = Arena.GetStringSlice(valueNode).Offset;
        var valueEnd = Arena.GetStringSlice(valueNode).Offset + Arena.GetStringSlice(valueNode).Length;
        if (valueStart < 0 || valueEnd > utf8Yaml.Length || valueStart > valueEnd)
        {
            return "false";
        }

        var valueSpan = Arena.GetStringValue(valueNode);
        if (Arena.GetStringQuoted(valueNode))
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

        var style = FixFormatting.DetectQuoteStyle(utf8Yaml, Arena.GetStringRange(valueNode), Arena.GetStringQuoted(valueNode));
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

    private static string GetStepKeyIndentation(byte[] utf8Yaml, int lineNumber)
    {
        var baseIndent = FixFormatting.GetLineIndentation(utf8Yaml, lineNumber);
        var lineStart = Utf8YamlLineHelpers.FindLineStartOffset(utf8Yaml, lineNumber);
        var offset = lineStart + baseIndent.Length;
        return offset + 1 < utf8Yaml.Length && utf8Yaml[offset] == (byte)'-' && utf8Yaml[offset + 1] == (byte)' '
            ? baseIndent + "  "
            : baseIndent;
    }

    private static bool LineContainsFlowMappingAt(byte[] utf8Yaml, int lineNumber, string keyIndent)
    {
        var lineStart = Utf8YamlLineHelpers.FindLineStartOffset(utf8Yaml, lineNumber);
        var lineEnd = lineStart;
        while (lineEnd < utf8Yaml.Length && utf8Yaml[lineEnd] != (byte)'\n') lineEnd++;
        if (lineEnd > lineStart && utf8Yaml[lineEnd - 1] == (byte)'\r') lineEnd--;

        if (!Utf8YamlLineHelpers.ByteLineHasKeyAtIndent(utf8Yaml, lineStart, lineEnd, keyIndent, "with:"u8))
            return false;

        for (var i = lineStart; i < lineEnd; i++)
            if (utf8Yaml[i] == (byte)'{') return true;
        return false;
    }

    private int FindFirstInputLine(byte[] utf8Yaml, SliceMap<StringNodeId> inputs)
    {
        var firstLine = int.MaxValue;
        foreach (var pair in inputs)
        {
            var line = Utf8YamlLineHelpers.FindLineNumberFromOffset(utf8Yaml, Arena.GetStringSlice(pair.Value).Offset);
            if (line > 0 && line < firstLine)
            {
                firstLine = line;
            }
        }

        return firstLine == int.MaxValue ? -1 : firstLine;
    }

    /// <summary>Case-insensitive YAML boolean false check (false, False, FALSE).</summary>
    private static bool IsBooleanFalse(ReadOnlySpan<byte> value)
    {
        return value.Length == 5
               && (value[0] | 0x20) == (byte)'f'
               && (value[1] | 0x20) == (byte)'a'
               && (value[2] | 0x20) == (byte)'l'
               && (value[3] | 0x20) == (byte)'s'
               && (value[4] | 0x20) == (byte)'e';
    }
}
