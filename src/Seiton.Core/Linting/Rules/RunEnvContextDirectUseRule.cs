using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using System.Text;

namespace Seiton.Core.Linting.Rules;

public sealed class RunEnvContextDirectUseRule : RuleBase
{
    public override string Id => "run-env-context-direct-use";

    public override string Name => "Run Env Context Direct Use Rule";

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecRun run)
        {
            return;
        }

        CheckRunNode(step, run, run.Run);
    }

    void CheckRunNode(Step step, ExecRun run, StringNode runNode)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        var runText = runNode.Value.AsSpan(Config.Utf8Yaml);
        var searchStart = 0;
        while (TryFindExpression(runText, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;

            var expression = TrimAsciiWhiteSpace(runText.Slice(bodyStart, bodyLength));
            if (expression.Length == 0)
            {
                continue;
            }

            var parseResult = ExpressionParser.Parse(expression);
            if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
            {
                continue;
            }

            if (!ContainsEnvRootReference(
                parseResult.RootNode,
                parentId: -1,
                parseResult.Nodes,
                parseResult.Arguments,
                expression))
            {
                continue;
            }

            if (TryBuildFix(run, runNode, expression, bodyStart, nextSearchStart - (bodyStart - 3), out var fix))
            {
                AddStepError(
                    step,
                    "run script must not reference ${{ env.* }} directly; use shell variables instead (e.g. $NAME or $env:NAME)",
                    runNode.Range,
                    fix);
            }
            else
            {
                AddStepError(
                    step,
                    "run script must not reference ${{ env.* }} directly; use shell variables instead (e.g. $NAME or $env:NAME)",
                    runNode.Range);
            }

            return;
        }
    }

    bool TryBuildFix(ExecRun run, StringNode runNode, ReadOnlySpan<byte> expression, int expressionBodyStart, int expressionLength, out DiagnosticFix fix)
    {
        fix = default;
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        if (!TryParseSimpleEnvReference(expression, out var variableName))
        {
            return false;
        }

        var replacement = IsPowerShell(run.Shell, Config.Utf8Yaml)
            ? "$env:" + variableName
            : "${" + variableName + "}";

        var absoluteOffset = runNode.Value.Offset + expressionBodyStart - 3;
        fix = new DiagnosticFix(
            "replace direct env context expansion with shell variable",
            [new TextEdit(absoluteOffset, expressionLength, replacement)]);
        return true;
    }

    static bool IsPowerShell(StringNode? shellNode, byte[] utf8Yaml)
    {
        if (shellNode is null || shellNode.Expression is not null)
        {
            return false;
        }

        var shell = Encoding.UTF8.GetString(shellNode.Value.AsSpan(utf8Yaml));
        return string.Equals(shell, "pwsh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase);
    }

    static bool TryParseSimpleEnvReference(ReadOnlySpan<byte> expression, out string variableName)
    {
        variableName = string.Empty;
        var index = 0;
        if (!ConsumeWordIgnoreCase(expression, ref index, "env"u8))
        {
            return false;
        }

        SkipWhiteSpace(expression, ref index);
        if (index >= expression.Length)
        {
            return false;
        }

        if (expression[index] == (byte)'.')
        {
            index++;
            if (!TryReadIdentifier(expression, ref index, out variableName))
            {
                return false;
            }

            SkipWhiteSpace(expression, ref index);
            return index == expression.Length;
        }

        if (expression[index] != (byte)'[')
        {
            return false;
        }

        index++;
        SkipWhiteSpace(expression, ref index);
        if (index >= expression.Length)
        {
            return false;
        }

        var quote = expression[index];
        if (quote is not ((byte)'\'' or (byte)'"'))
        {
            return false;
        }

        index++;
        var start = index;
        while (index < expression.Length && expression[index] != quote)
        {
            index++;
        }

        if (index >= expression.Length)
        {
            return false;
        }

        var nameBytes = expression[start..index];
        index++;
        SkipWhiteSpace(expression, ref index);
        if (index >= expression.Length || expression[index] != (byte)']')
        {
            return false;
        }

        index++;
        SkipWhiteSpace(expression, ref index);
        if (index != expression.Length)
        {
            return false;
        }

        var name = Encoding.UTF8.GetString(nameBytes);
        if (!IsSimpleIdentifier(name))
        {
            return false;
        }

        variableName = name;
        return true;
    }

    static bool ConsumeWordIgnoreCase(ReadOnlySpan<byte> value, ref int index, ReadOnlySpan<byte> word)
    {
        if (index + word.Length > value.Length)
        {
            return false;
        }

        for (var i = 0; i < word.Length; i++)
        {
            var l = value[index + i];
            var r = word[i];
            if (l is >= (byte)'A' and <= (byte)'Z')
            {
                l = (byte)(l + 32);
            }

            if (l != r)
            {
                return false;
            }
        }

        index += word.Length;
        return true;
    }

    static void SkipWhiteSpace(ReadOnlySpan<byte> value, ref int index)
    {
        while (index < value.Length && IsWhiteSpace(value[index]))
        {
            index++;
        }
    }

    static bool TryReadIdentifier(ReadOnlySpan<byte> value, ref int index, out string identifier)
    {
        identifier = string.Empty;
        if (index >= value.Length || !IsIdentifierStart(value[index]))
        {
            return false;
        }

        var start = index;
        index++;
        while (index < value.Length && IsIdentifierPart(value[index]))
        {
            index++;
        }

        identifier = Encoding.UTF8.GetString(value[start..index]);
        return true;
    }

    static bool IsSimpleIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (!IsIdentifierStart((byte)value[0]))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (!IsIdentifierPart((byte)value[i]))
            {
                return false;
            }
        }

        return true;
    }

    static bool IsIdentifierStart(byte b)
    {
        return (b >= (byte)'A' && b <= (byte)'Z')
            || (b >= (byte)'a' && b <= (byte)'z')
            || b == (byte)'_';
    }

    static bool IsIdentifierPart(byte b)
    {
        return IsIdentifierStart(b) || (b >= (byte)'0' && b <= (byte)'9');
    }

    static bool ContainsEnvRootReference(
        int nodeId,
        int parentId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind == ExpressionNodeKind.Identifier
            && IsContextRootIdentifier(nodeId, parentId, nodes)
            && EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), "env"u8))
        {
            return true;
        }

        return node.Kind switch
        {
            ExpressionNodeKind.Unary => ContainsEnvRootReference(node.Left, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.Binary => ContainsEnvRootReference(node.Left, nodeId, nodes, arguments, expression)
                || ContainsEnvRootReference(node.Right, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.MemberAccess => ContainsEnvRootReference(node.Left, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.WildcardAccess => ContainsEnvRootReference(node.Left, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.IndexAccess => ContainsEnvRootReference(node.Left, nodeId, nodes, arguments, expression)
                || ContainsEnvRootReference(node.Right, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.FunctionCall => ContainsEnvRootReferenceInFunction(node, nodeId, nodes, arguments, expression),
            _ => false,
        };
    }

    static bool ContainsEnvRootReferenceInFunction(
        ExpressionNode functionCallNode,
        int functionCallNodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression)
    {
        if (ContainsEnvRootReference(functionCallNode.Left, functionCallNodeId, nodes, arguments, expression))
        {
            return true;
        }

        for (var i = 0; i < functionCallNode.ArgCount; i++)
        {
            var argIndex = functionCallNode.ArgStart + i;
            if (argIndex < 0 || argIndex >= arguments.Length)
            {
                continue;
            }

            if (ContainsEnvRootReference(arguments[argIndex], functionCallNodeId, nodes, arguments, expression))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsContextRootIdentifier(int nodeId, int parentId, ExpressionNode[] nodes)
    {
        if (parentId < 0)
        {
            return true;
        }

        if (parentId >= nodes.Length)
        {
            return false;
        }

        var parent = nodes[parentId];
        return parent.Left == nodeId
            && (parent.Kind == ExpressionNodeKind.MemberAccess
                || parent.Kind == ExpressionNodeKind.IndexAccess
                || parent.Kind == ExpressionNodeKind.WildcardAccess);
    }

    static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = right[i];
            if (l is >= (byte)'A' and <= (byte)'Z')
            {
                l = (byte)(l + 32);
            }

            if (r is >= (byte)'A' and <= (byte)'Z')
            {
                r = (byte)(r + 32);
            }

            if (l != r)
            {
                return false;
            }
        }

        return true;
    }

    static bool TryFindExpression(
        ReadOnlySpan<byte> value,
        int searchStart,
        out int bodyStart,
        out int bodyLength,
        out int nextSearchStart)
    {
        bodyStart = 0;
        bodyLength = 0;
        nextSearchStart = 0;

        if ((uint)searchStart >= (uint)value.Length)
        {
            return false;
        }

        var start = value[searchStart..].IndexOf("${{"u8);
        if (start < 0)
        {
            return false;
        }

        bodyStart = searchStart + start + 3;
        var close = value[bodyStart..].IndexOf("}}"u8);
        if (close < 0)
        {
            return false;
        }

        bodyLength = close;
        nextSearchStart = bodyStart + close + 2;
        return true;
    }

    static ReadOnlySpan<byte> TrimAsciiWhiteSpace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length - 1;
        while (start <= end && IsWhiteSpace(value[start]))
        {
            start++;
        }

        while (end >= start && IsWhiteSpace(value[end]))
        {
            end--;
        }

        return end < start ? [] : value.Slice(start, end - start + 1);
    }

    static bool IsWhiteSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
