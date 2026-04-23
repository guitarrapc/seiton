using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class WorkflowSecretsRule() : RuleBase(RuleId.WorkflowSecrets)
{
    public override string Name => "Workflow Secrets Rule";

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);

        if (Config.Utf8Yaml is null || workflow.Jobs.Count < 2)
        {
            return;
        }

        CheckEnv(workflow.Env, workflow);
    }

    private void CheckEnv(Env? env, Workflow workflow)
    {
        if (env?.Vars is null || env.Vars.Value.Count == 0 || Config.Utf8Yaml is null)
        {
            return;
        }

        foreach (var pair in env.Vars)
        {
            var envVar = pair.Value;
            if (!ContainsSecretsOrGitHubTokenReference(envVar.Value))
            {
                continue;
            }

            var envName = Decode(Arena.GetStringSlice(envVar.Name));
            AddWorkflowError(
                workflow,
                $"workflow env '{envName}' must not set secrets.* or github.token when workflow has multiple jobs; move secret mapping to job/step env",
                Arena.GetStringRange(envVar.Value));
        }
    }

    private bool ContainsSecretsOrGitHubTokenReference(StringNodeId node)
    {
        if (Config.Utf8Yaml is null)
        {
            return false;
        }

        if (ContainsReferenceInValue(Arena.GetStringValue(node)))
        {
            return true;
        }

        if (!Arena.GetStringExpression(node).HasValue)
        {
            return false;
        }

        var expression = TrimAsciiWhiteSpace(Arena.GetStringValue(Arena.GetStringExpression(node)));
        return ContainsReferenceInExpression(expression);
    }

    private bool ContainsReferenceInValue(ReadOnlySpan<byte> value)
    {
        var searchStart = 0;
        while (TryFindExpression(value, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;
            var expression = TrimAsciiWhiteSpace(value.Slice(bodyStart, bodyLength));
            if (ContainsReferenceInExpression(expression))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsReferenceInExpression(ReadOnlySpan<byte> expression)
    {
        if (expression.Length == 0)
        {
            return false;
        }

        var parseResult = Config.ParseExpression(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return false;
        }

        return ContainsSecretsReference(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression)
            || ContainsGitHubTokenReference(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);
    }

    private static bool ContainsSecretsReference(int nodeId, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind == ExpressionNodeKind.Identifier
            && EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), "secrets"u8))
        {
            return true;
        }

        return ContainsReferenceInChildren(node, nodeId, nodes, arguments, expression, ContainsSecretsReference);
    }

    private static bool ContainsGitHubTokenReference(int nodeId, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (IsGitHubTokenAccess(node, nodes, expression))
        {
            return true;
        }

        return ContainsReferenceInChildren(node, nodeId, nodes, arguments, expression, ContainsGitHubTokenReference);
    }

    private static bool IsGitHubTokenAccess(ExpressionNode node, ExpressionNode[] nodes, ReadOnlySpan<byte> expression)
    {
        if (node.Kind == ExpressionNodeKind.MemberAccess)
        {
            if (node.Left >= 0
                && node.Left < nodes.Length)
            {
                var left = nodes[node.Left];
                if (left.Kind == ExpressionNodeKind.Identifier
                    && EqualsAsciiIgnoreCase(left.Token.AsSpan(expression), "github"u8)
                    && EqualsAsciiIgnoreCase(node.Token.AsSpan(expression), "token"u8))
                {
                    return true;
                }
            }
        }

        if (node.Kind == ExpressionNodeKind.IndexAccess)
        {
            if (node.Left >= 0
                && node.Left < nodes.Length
                && node.Right >= 0
                && node.Right < nodes.Length)
            {
                var left = nodes[node.Left];
                var right = nodes[node.Right];
                if (left.Kind == ExpressionNodeKind.Identifier
                    && EqualsAsciiIgnoreCase(left.Token.AsSpan(expression), "github"u8)
                    && right.Kind == ExpressionNodeKind.StringLiteral
                    && EqualsAsciiIgnoreCase(right.Token.AsSpan(expression), "token"u8))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private delegate bool NodeMatcher(int nodeId, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression);

    private static bool ContainsReferenceInChildren(
        ExpressionNode node,
        int parentNodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        NodeMatcher matcher)
    {
        return node.Kind switch
        {
            ExpressionNodeKind.Unary => matcher(node.Left, nodes, arguments, expression),
            ExpressionNodeKind.Binary => matcher(node.Left, nodes, arguments, expression)
                || matcher(node.Right, nodes, arguments, expression),
            ExpressionNodeKind.MemberAccess => matcher(node.Left, nodes, arguments, expression)
                || matcher(node.Right, nodes, arguments, expression),
            ExpressionNodeKind.WildcardAccess => matcher(node.Left, nodes, arguments, expression),
            ExpressionNodeKind.IndexAccess => matcher(node.Left, nodes, arguments, expression)
                || matcher(node.Right, nodes, arguments, expression),
            ExpressionNodeKind.FunctionCall => ContainsReferenceInFunctionCall(node, parentNodeId, nodes, arguments, expression, matcher),
            _ => false,
        };
    }

    private static bool ContainsReferenceInFunctionCall(
        ExpressionNode functionCallNode,
        int functionCallNodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        NodeMatcher matcher)
    {
        if (matcher(functionCallNode.Left, nodes, arguments, expression))
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

            if (matcher(arguments[argIndex], nodes, arguments, expression))
            {
                return true;
            }
        }

        return false;
    }
}
