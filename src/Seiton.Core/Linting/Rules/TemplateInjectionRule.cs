using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class TemplateInjectionRule : RuleBase
{
    public override string Id => "template-injection";

    public override string Name => "Template Injection Rule";

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        if (step.Exec is ExecRun run)
        {
            CheckSink(step, run.Run, "run");
        }
    }

    void CheckSink(Step step, StringNode? valueNode, string sinkName)
    {
        if (valueNode is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var value = valueNode.Value.AsSpan(Config.Utf8Yaml);
        var searchStart = 0;
        while (TryFindExpression(value, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;

            var expression = TrimAsciiWhiteSpace(value.Slice(bodyStart, bodyLength));
            if (expression.Length == 0)
            {
                continue;
            }

            var parseResult = Config.ParseExpression(expression);
            if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
            {
                continue;
            }

            if (!ContainsUntrustedEventReference(parseResult, expression))
            {
                continue;
            }

            AddStepError(
                step,
                $"template injection risk: {sinkName} contains expression referencing untrusted github.event data",
                valueNode.Range);
            return;
        }
    }

    static bool ContainsUntrustedEventReference(ExpressionParseResult parseResult, ReadOnlySpan<byte> expression)
    {
        return ContainsUntrustedEventReference(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);
    }

    static bool ContainsUntrustedEventReference(int nodeId, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        if (IsGithubEventReference(nodeId, nodes, expression))
        {
            return true;
        }

        var node = nodes[nodeId];
        return node.Kind switch
        {
            ExpressionNodeKind.Unary => ContainsUntrustedEventReference(node.Left, nodes, arguments, expression),
            ExpressionNodeKind.Binary => ContainsUntrustedEventReference(node.Left, nodes, arguments, expression)
                || ContainsUntrustedEventReference(node.Right, nodes, arguments, expression),
            ExpressionNodeKind.MemberAccess => ContainsUntrustedEventReference(node.Left, nodes, arguments, expression),
            ExpressionNodeKind.WildcardAccess => ContainsUntrustedEventReference(node.Left, nodes, arguments, expression),
            ExpressionNodeKind.IndexAccess => ContainsUntrustedEventReference(node.Left, nodes, arguments, expression)
                || ContainsUntrustedEventReference(node.Right, nodes, arguments, expression),
            ExpressionNodeKind.FunctionCall => ContainsUntrustedEventReferenceInFunction(node, nodes, arguments, expression),
            _ => false,
        };
    }

    static bool ContainsUntrustedEventReferenceInFunction(ExpressionNode functionCallNode, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression)
    {
        if (ContainsUntrustedEventReference(functionCallNode.Left, nodes, arguments, expression))
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

            if (ContainsUntrustedEventReference(arguments[argIndex], nodes, arguments, expression))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsGithubEventReference(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        switch (node.Kind)
        {
            case ExpressionNodeKind.MemberAccess:
                if (TokenEqualsIgnoreCase(node.Token.AsSpan(expression), "event"u8)
                    && IsIdentifier(node.Left, nodes, expression, "github"u8))
                {
                    return true;
                }

                return IsGithubEventReference(node.Left, nodes, expression);

            case ExpressionNodeKind.IndexAccess:
                if (IsIdentifier(node.Left, nodes, expression, "github"u8)
                    && IsEventIndex(node.Right, nodes, expression))
                {
                    return true;
                }

                return IsGithubEventReference(node.Left, nodes, expression);

            case ExpressionNodeKind.WildcardAccess:
                return IsGithubEventReference(node.Left, nodes, expression);

            default:
                return false;
        }
    }

    static bool IsIdentifier(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression, ReadOnlySpan<byte> expected)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        return node.Kind == ExpressionNodeKind.Identifier
            && TokenEqualsIgnoreCase(node.Token.AsSpan(expression), expected);
    }

    static bool IsEventIndex(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind is ExpressionNodeKind.StringLiteral or ExpressionNodeKind.Identifier)
        {
            return TokenEqualsIgnoreCase(node.Token.AsSpan(expression), "event"u8);
        }

        return false;
    }

    static bool TokenEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
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
}
