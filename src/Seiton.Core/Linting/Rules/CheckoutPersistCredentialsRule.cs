using Seiton.Core.Generated;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using System.Text;

namespace Seiton.Core.Linting.Rules;

public sealed class CheckoutPersistCredentialsRule : RuleBase
{
    const string PersistCredentialsKey = "persist-credentials";
    const string FixHint = "review later authenticated git commands; for example, git push may require explicit auth setup such as git remote set-url origin ...";

    public override string Id => "checkout-persist-credentials";

    public override string Name => "Checkout Persist Credentials Rule";

    public override void VisitStep(Step step)
    {
        if (step.Exec is not ExecAction actionExec || Config.Utf8Yaml is null)
        {
            return;
        }

        var usesText = actionExec.Uses.Value.AsSpan(Config.Utf8Yaml);
        if (!PopularActions.TryGet(usesText, out var actionSpec) || actionSpec.Id != PopularActions.ActionId.ActionsCheckout)
        {
            return;
        }

        var actionRef = Decode(actionExec.Uses.Value);
        var message = BuildMessage(actionRef);

        if (actionExec.Inputs is null || !actionExec.Inputs.TryGetValue(Utf8String.FromLowerAscii("persist-credentials"u8), out var persistCredentialsNode))
        {
            if (TryBuildMissingInputFix(step, actionExec, Config.Utf8Yaml, out var missingFix))
            {
                AddStepWarning(step, message, BuildStepLocation(step), missingFix);
                return;
            }

            AddStepWarning(step, message);
            return;
        }

        var value = persistCredentialsNode.Value.AsSpan(Config.Utf8Yaml);
        if (persistCredentialsNode.Expression is null && value.IndexOf("${{"u8) < 0 && value.SequenceEqual("false"u8))
        {
            return;
        }

        if (TryBuildValueReplacementFix(persistCredentialsNode, Config.Utf8Yaml, out var valueFix))
        {
            AddStepWarning(step, message, persistCredentialsNode.Range, valueFix);
            return;
        }

        AddStepWarning(step, message, persistCredentialsNode.Range);
    }

    static string BuildMessage(string actionRef)
    {
        return $"action '{actionRef}' should set with.persist-credentials to false to avoid persisting credentials in .git/config; after changing this, {FixHint}";
    }

    static bool TryBuildValueReplacementFix(StringNode persistCredentialsNode, byte[] utf8Yaml, out DiagnosticFix fix)
    {
        fix = default;
        var value = persistCredentialsNode.Value.AsSpan(utf8Yaml);
        if (persistCredentialsNode.Expression is not null || value.IndexOf("${{"u8) >= 0)
        {
            return false;
        }

        var replacement = BuildReplacementText(persistCredentialsNode, utf8Yaml);
        fix = new DiagnosticFix(
            $"set with.{PersistCredentialsKey} to false; {FixHint}",
            [new TextEdit(persistCredentialsNode.Value.Offset, persistCredentialsNode.Value.Length, replacement)]);
        return true;
    }

    static bool TryBuildMissingInputFix(Step step, ExecAction actionExec, byte[] utf8Yaml, out DiagnosticFix fix)
    {
        fix = default;

        var sourceText = Encoding.UTF8.GetString(utf8Yaml);
        var normalized = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length == 0)
        {
            return false;
        }

        var usesLine = FindLineNumberFromOffset(utf8Yaml, actionExec.Uses.Value.Offset);
        if (usesLine < 1 || usesLine > lines.Length)
        {
            return false;
        }

        var stepEndLine = step.Range.EndLine > 0
            ? Math.Min(lines.Length, Math.Max(usesLine, step.Range.EndLine))
            : lines.Length;
        var lineEnding = FixFormatting.DetectDominantLineEnding(utf8Yaml);
        var keyIndent = GetStepKeyIndentation(sourceText, usesLine);

        if (actionExec.Inputs is not null && actionExec.Inputs.Count > 0)
        {
            var withLine = FindWithLine(lines, usesLine, stepEndLine, keyIndent);
            if (withLine < 0 || LineContainsFlowMapping(lines[withLine - 1], keyIndent))
            {
                return false;
            }

            var firstInputLine = FindFirstInputLine(utf8Yaml, actionExec.Inputs);
            if (firstInputLine < 1 || firstInputLine > lines.Length)
            {
                return false;
            }

            var inputIndent = FixFormatting.GetLineIndentation(sourceText, firstInputLine);
            var insertOffset = FindLineStartOffset(utf8Yaml, firstInputLine);
            var insertText = inputIndent + PersistCredentialsKey + ": false" + lineEnding;

            fix = new DiagnosticFix(
                $"insert with.{PersistCredentialsKey}: false; {FixHint}",
                [new TextEdit(insertOffset, 0, insertText)]);
            return true;
        }

        var withIndent = keyIndent;
        var childIndent = withIndent + FixFormatting.InferIndentationUnit(sourceText);
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

    static string BuildReplacementText(StringNode valueNode, byte[] utf8Yaml)
    {
        var valueStart = valueNode.Value.Offset;
        var valueEnd = valueNode.Value.Offset + valueNode.Value.Length;
        if (valueStart < 0 || valueEnd > utf8Yaml.Length || valueStart > valueEnd)
        {
            return "false";
        }

        var valueSpan = valueNode.Value.AsSpan(utf8Yaml);
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

    static string GetStepKeyIndentation(string sourceText, int lineNumber)
    {
        var line = GetLine(sourceText, lineNumber);
        var baseIndent = FixFormatting.GetLineIndentation(sourceText, lineNumber);
        if (line.Length < baseIndent.Length + 2)
        {
            return baseIndent;
        }

        return line.AsSpan(baseIndent.Length).StartsWith("- ", StringComparison.Ordinal)
            ? baseIndent + "  "
            : baseIndent;
    }

    static int FindWithLine(string[] lines, int usesLine, int stepEndLine, string keyIndent)
    {
        var maxLine = Math.Min(lines.Length, stepEndLine);
        for (var lineNumber = Math.Max(usesLine + 1, 1); lineNumber <= maxLine; lineNumber++)
        {
            var line = lines[lineNumber - 1];
            if (!line.StartsWith(keyIndent, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[keyIndent.Length..].TrimStart();
            if (rest.StartsWith("with:", StringComparison.Ordinal))
            {
                return lineNumber;
            }
        }

        return -1;
    }

    static bool LineContainsFlowMapping(string line, string keyIndent)
    {
        if (!line.StartsWith(keyIndent, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = line[keyIndent.Length..].TrimStart();
        var braceIndex = rest.IndexOf('{', StringComparison.Ordinal);
        return rest.StartsWith("with:", StringComparison.Ordinal) && braceIndex >= 0;
    }

    static int FindFirstInputLine(byte[] utf8Yaml, IReadOnlyDictionary<Utf8String, StringNode> inputs)
    {
        var firstLine = int.MaxValue;
        foreach (var pair in inputs)
        {
            var line = FindLineNumberFromOffset(utf8Yaml, pair.Value.Value.Offset);
            if (line > 0 && line < firstLine)
            {
                firstLine = line;
            }
        }

        return firstLine == int.MaxValue ? -1 : firstLine;
    }

    static string GetLine(string sourceText, int lineNumber)
    {
        var lines = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return lineNumber >= 1 && lineNumber <= lines.Length ? lines[lineNumber - 1] : string.Empty;
    }

    static int FindLineStartOffset(byte[] utf8Yaml, int lineNumber)
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

    static int FindLineEndOffsetIncludingNewLine(byte[] utf8Yaml, int lineNumber)
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

    static int FindLineNumberFromOffset(byte[] utf8Yaml, int offset)
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
