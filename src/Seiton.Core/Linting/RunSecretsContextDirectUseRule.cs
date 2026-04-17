using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public sealed class RunSecretsContextDirectUseRule : RuleBase
{
    public override string Id => "run-secrets-context-direct-use";

    public override string Name => "Run Secrets Context Direct Use Rule";

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null || step.Exec is not ExecRun run)
        {
            return;
        }

        CheckRunNode(step, run.Run);
    }

    void CheckRunNode(Step step, StringNode runNode)
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

            if (!ContainsSecretsRootReference(
                parseResult.RootNode,
                parentId: -1,
                parseResult.Nodes,
                parseResult.Arguments,
                expression))
            {
                continue;
            }

            AddStepError(
                step,
                "run script must not reference ${{ secrets.* }} directly; map secrets to env and use shell variables instead (e.g. ${TOKEN}, $TOKEN, or $env:TOKEN)",
                runNode.Range);
            return;
        }
    }

    static bool ContainsSecretsRootReference(
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
            && EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), "secrets"u8))
        {
            return true;
        }

        return node.Kind switch
        {
            ExpressionNodeKind.Unary => ContainsSecretsRootReference(node.Left, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.Binary => ContainsSecretsRootReference(node.Left, nodeId, nodes, arguments, expression)
                || ContainsSecretsRootReference(node.Right, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.MemberAccess => ContainsSecretsRootReference(node.Left, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.WildcardAccess => ContainsSecretsRootReference(node.Left, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.IndexAccess => ContainsSecretsRootReference(node.Left, nodeId, nodes, arguments, expression)
                || ContainsSecretsRootReference(node.Right, nodeId, nodes, arguments, expression),
            ExpressionNodeKind.FunctionCall => ContainsSecretsRootReferenceInFunction(node, nodeId, nodes, arguments, expression),
            _ => false,
        };
    }

    static bool ContainsSecretsRootReferenceInFunction(
        ExpressionNode functionCallNode,
        int functionCallNodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression)
    {
        if (ContainsSecretsRootReference(functionCallNode.Left, functionCallNodeId, nodes, arguments, expression))
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

            if (ContainsSecretsRootReference(arguments[argIndex], functionCallNodeId, nodes, arguments, expression))
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
