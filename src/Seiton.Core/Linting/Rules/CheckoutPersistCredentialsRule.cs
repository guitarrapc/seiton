using Seiton.Core.Generated;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags <c>actions/checkout</c> usage without <c>persist-credentials: false</c>.</summary>
public sealed class CheckoutPersistCredentialsRule() : RuleBase(RuleId.CheckoutPersistCredentials)
{
    private const string PersistCredentialsKey = "persist-credentials";
    private const string FixHint = "review later authenticated git commands; for example, git push may require explicit auth setup such as git remote set-url origin ...";

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
            if (Config.Fix.Enabled && Config.Utf8Yaml is not null && TryBuildMissingInputFix(Config, step, actionExec, Config.Utf8Yaml, out var missingFix))
            {
                AddStepWarning(step, message, BuildStepLocation(step), missingFix);
                return;
            }

            AddStepWarning(step, message);
            return;
        }

        var value = Arena.GetStringValue(persistCredentialsNode);
        if (!Arena.GetStringExpression(persistCredentialsNode).HasValue && value.IndexOf("${{"u8) < 0 && value.SequenceEqual("false"u8))
        {
            return;
        }

        if (Config.Fix.Enabled && Config.Utf8Yaml is not null && TryBuildValueReplacementFix(Config, persistCredentialsNode, Config.Utf8Yaml, out var valueFix))
        {
            AddStepWarning(step, message, Arena.GetStringRange(persistCredentialsNode), valueFix);
            return;
        }

        AddStepWarning(step, message, Arena.GetStringRange(persistCredentialsNode));
    }

    private static string BuildMessage(string actionRef)
    {
        return $"action '{actionRef}' should set with.persist-credentials to false to avoid persisting credentials in .git/config; after changing this, {FixHint}";
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

    private bool TryBuildValueReplacementFix(LintConfig config, StringNodeId persistCredentialsNode, byte[] utf8Yaml, out DiagnosticFix fix)
    {
        fix = default;
        var value = Arena.GetStringValue(persistCredentialsNode);
        if (Arena.GetStringExpression(persistCredentialsNode).HasValue || value.IndexOf("${{"u8) >= 0)
        {
            return false;
        }

        var replacement = BuildReplacementText(persistCredentialsNode, utf8Yaml);
        fix = new DiagnosticFix(
            $"set with.{PersistCredentialsKey} to false; {FixHint}",
            [new TextEdit(Arena.GetStringSlice(persistCredentialsNode).Offset, Arena.GetStringSlice(persistCredentialsNode).Length, replacement)]);
        return true;
    }

    private bool TryBuildMissingInputFix(LintConfig config, Step step, ExecAction actionExec, byte[] utf8Yaml, out DiagnosticFix fix)
    {
        fix = default;

        if (utf8Yaml.Length == 0)
        {
            return false;
        }

        var usesLine = FindLineNumberFromOffset(utf8Yaml, Arena.GetStringSlice(actionExec.Uses).Offset);
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
            var withLine = FindWithLine(utf8Yaml, usesLine, stepEndLine, keyIndent);
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
            var insertOffset = FindLineStartOffset(utf8Yaml, firstInputLine);
            var insertText = inputIndent + PersistCredentialsKey + ": false" + lineEnding;

            fix = new DiagnosticFix(
                $"insert with.{PersistCredentialsKey}: false; {FixHint}",
                [new TextEdit(insertOffset, 0, insertText)]);
            return true;
        }

        var withIndent = keyIndent;
        var childIndent = withIndent + FixFormatting.InferIndentationUnit(utf8Yaml);
        var insertAfterUsesOffset = FindLineEndOffsetIncludingNewLine(utf8Yaml, usesLine);
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
        var lineStart = FindLineStartOffset(utf8Yaml, lineNumber);
        var offset = lineStart + baseIndent.Length;
        return offset + 1 < utf8Yaml.Length && utf8Yaml[offset] == (byte)'-' && utf8Yaml[offset + 1] == (byte)' '
            ? baseIndent + "  "
            : baseIndent;
    }

    private static int FindWithLine(byte[] utf8Yaml, int usesLine, int stepEndLine, string keyIndent)
    {
        var currentLine = 1;
        var pos = 0;
        var startLine = Math.Max(usesLine + 1, 1);
        while (currentLine < startLine && pos < utf8Yaml.Length)
            if (utf8Yaml[pos++] == (byte)'\n') currentLine++;

        while (currentLine <= stepEndLine && pos <= utf8Yaml.Length)
        {
            if (pos >= utf8Yaml.Length) break;
            var lineStart = pos;
            while (pos < utf8Yaml.Length && utf8Yaml[pos] != (byte)'\n') pos++;
            var lineEnd = pos;
            if (lineEnd > lineStart && utf8Yaml[lineEnd - 1] == (byte)'\r') lineEnd--;
            if (pos < utf8Yaml.Length) pos++;

            if (ByteLineHasKeyAtIndent(utf8Yaml, lineStart, lineEnd, keyIndent, "with:"u8))
                return currentLine;

            currentLine++;
        }
        return -1;
    }

    private static bool LineContainsFlowMappingAt(byte[] utf8Yaml, int lineNumber, string keyIndent)
    {
        var lineStart = FindLineStartOffset(utf8Yaml, lineNumber);
        var lineEnd = lineStart;
        while (lineEnd < utf8Yaml.Length && utf8Yaml[lineEnd] != (byte)'\n') lineEnd++;
        if (lineEnd > lineStart && utf8Yaml[lineEnd - 1] == (byte)'\r') lineEnd--;

        if (!ByteLineHasKeyAtIndent(utf8Yaml, lineStart, lineEnd, keyIndent, "with:"u8))
            return false;

        for (var i = lineStart; i < lineEnd; i++)
            if (utf8Yaml[i] == (byte)'{') return true;
        return false;
    }

    // Checks if the line [lineStart..lineEnd) starts with keyIndent (ASCII), followed by optional
    // whitespace and then the given keyBytes prefix.
    private static bool ByteLineHasKeyAtIndent(byte[] utf8Yaml, int lineStart, int lineEnd, string keyIndent, ReadOnlySpan<byte> keyBytes)
    {
        if (lineEnd - lineStart < keyIndent.Length) return false;
        for (var k = 0; k < keyIndent.Length; k++)
            if (utf8Yaml[lineStart + k] != (byte)keyIndent[k]) return false;
        var idx = lineStart + keyIndent.Length;
        while (idx < lineEnd && (utf8Yaml[idx] == (byte)' ' || utf8Yaml[idx] == (byte)'\t')) idx++;
        var remaining = lineEnd - idx;
        if (remaining < keyBytes.Length) return false;
        return utf8Yaml.AsSpan(idx, keyBytes.Length).SequenceEqual(keyBytes);
    }

    private int FindFirstInputLine(byte[] utf8Yaml, SliceMap<StringNodeId> inputs)
    {
        var firstLine = int.MaxValue;
        foreach (var pair in inputs)
        {
            var line = FindLineNumberFromOffset(utf8Yaml, Arena.GetStringSlice(pair.Value).Offset);
            if (line > 0 && line < firstLine)
            {
                firstLine = line;
            }
        }

        return firstLine == int.MaxValue ? -1 : firstLine;
    }

    private static string GetLine(string sourceText, int lineNumber)
    {
        // no longer called from the hot path; retained for safety
        var lines = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return lineNumber >= 1 && lineNumber <= lines.Length ? lines[lineNumber - 1] : string.Empty;
    }

    private static int FindLineStartOffset(byte[] utf8Yaml, int lineNumber)
    {
        if (lineNumber <= 1)
        {
            return 0;
        }

        var currentLine = 1;
        for (var i = 0; i < utf8Yaml.Length; i++)
        {
            if (utf8Yaml[i] != (byte)'\n')
            {
                continue;
            }

            currentLine++;
            if (currentLine == lineNumber)
            {
                return i + 1;
            }
        }

        return utf8Yaml.Length;
    }

    private static int FindLineEndOffsetIncludingNewLine(byte[] utf8Yaml, int lineNumber)
    {
        var start = FindLineStartOffset(utf8Yaml, lineNumber);
        for (var i = start; i < utf8Yaml.Length; i++)
        {
            if (utf8Yaml[i] == (byte)'\n')
            {
                return i + 1;
            }
        }

        return utf8Yaml.Length;
    }

    private static int FindLineNumberFromOffset(byte[] utf8Yaml, int offset)
    {
        if (offset <= 0)
        {
            return 1;
        }

        if (offset > utf8Yaml.Length)
        {
            offset = utf8Yaml.Length;
        }

        var lineNumber = 1;
        for (var i = 0; i < offset; i++)
        {
            if (utf8Yaml[i] == (byte)'\n')
            {
                lineNumber++;
            }
        }

        return lineNumber;
    }
}
