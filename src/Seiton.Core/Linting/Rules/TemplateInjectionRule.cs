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

    private void CheckSink(Step step, StringNode? valueNode, string sinkName)
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
                $"template injection risk: {sinkName} contains expression referencing untrusted github.event/github context data",
                valueNode.Range);
            return;
        }
    }

    private static bool ContainsUntrustedEventReference(ExpressionParseResult parseResult, ReadOnlySpan<byte> expression)
    {
        return ContainsUntrustedEventReference(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression, safeDepth: 0);
    }

    private static bool ContainsUntrustedEventReference(int nodeId, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression, int safeDepth)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        if (safeDepth == 0 && IsUntrustedReference(nodeId, nodes, expression))
        {
            return true;
        }

        var node = nodes[nodeId];
        return node.Kind switch
        {
            ExpressionNodeKind.Unary => ContainsUntrustedEventReference(node.Left, nodes, arguments, expression, safeDepth),
            ExpressionNodeKind.Binary => ContainsUntrustedEventReference(node.Left, nodes, arguments, expression, safeDepth)
                || ContainsUntrustedEventReference(node.Right, nodes, arguments, expression, safeDepth),
            ExpressionNodeKind.MemberAccess => ContainsUntrustedEventReference(node.Left, nodes, arguments, expression, safeDepth),
            ExpressionNodeKind.WildcardAccess => ContainsUntrustedEventReference(node.Left, nodes, arguments, expression, safeDepth),
            ExpressionNodeKind.IndexAccess => ContainsUntrustedEventReference(node.Left, nodes, arguments, expression, safeDepth)
                || ContainsUntrustedEventReference(node.Right, nodes, arguments, expression, safeDepth),
            ExpressionNodeKind.FunctionCall => ContainsUntrustedEventReferenceInFunction(node, nodes, arguments, expression, safeDepth),
            _ => false,
        };
    }

    private static bool ContainsUntrustedEventReferenceInFunction(
        ExpressionNode functionCallNode,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        int safeDepth)
    {
        var calleeSafeDepth = safeDepth;
        if (IsSafeFunctionCall(functionCallNode, nodes, expression))
        {
            calleeSafeDepth++;
        }

        if (ContainsUntrustedEventReference(functionCallNode.Left, nodes, arguments, expression, safeDepth))
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

            if (ContainsUntrustedEventReference(arguments[argIndex], nodes, arguments, expression, calleeSafeDepth))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSafeFunctionCall(ExpressionNode functionCallNode, ExpressionNode[] nodes, ReadOnlySpan<byte> expression)
    {
        if (functionCallNode.Left < 0 || functionCallNode.Left >= nodes.Length)
        {
            return false;
        }

        var callee = nodes[functionCallNode.Left];
        if (callee.Kind != ExpressionNodeKind.Identifier)
        {
            return false;
        }

        var calleeName = callee.Token.AsSpan(expression);
        return TokenEqualsIgnoreCase(calleeName, "contains"u8)
            || TokenEqualsIgnoreCase(calleeName, "startswith"u8)
            || TokenEqualsIgnoreCase(calleeName, "endswith"u8);
    }

    private static bool IsUntrustedReference(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression)
    {
        Span<PathSegment> segments = stackalloc PathSegment[16];
        if (!TryBuildPathSegments(nodeId, nodes, expression, segments, out var count))
        {
            return false;
        }

        for (var i = 0; i < s_untrustedPaths.Length; i++)
        {
            if (IsPathMatch(segments[..count], s_untrustedPaths[i], expression))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildPathSegments(
        int nodeId,
        ExpressionNode[] nodes,
        ReadOnlySpan<byte> expression,
        Span<PathSegment> destination,
        out int count)
    {
        count = 0;
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        switch (node.Kind)
        {
            case ExpressionNodeKind.Identifier:
                destination[0] = new PathSegment(node.Token, false);
                count = 1;
                return true;
            case ExpressionNodeKind.MemberAccess:
                if (!TryBuildPathSegments(node.Left, nodes, expression, destination, out count))
                {
                    return false;
                }

                if (count >= destination.Length)
                {
                    return false;
                }

                destination[count++] = new PathSegment(node.Token, false);
                return true;
            case ExpressionNodeKind.WildcardAccess:
                if (!TryBuildPathSegments(node.Left, nodes, expression, destination, out count))
                {
                    return false;
                }

                if (count >= destination.Length)
                {
                    return false;
                }

                destination[count++] = new PathSegment(default, true);
                return true;
            case ExpressionNodeKind.IndexAccess:
                if (!TryBuildPathSegments(node.Left, nodes, expression, destination, out count))
                {
                    return false;
                }

                if (count >= destination.Length)
                {
                    return false;
                }

                if (TryGetIndexSegment(node.Right, nodes, out var token))
                {
                    destination[count++] = new PathSegment(token, false);
                }
                else
                {
                    destination[count++] = new PathSegment(default, true);
                }

                return true;
            default:
                return false;
        }
    }

    private static bool TryGetIndexSegment(int nodeId, ExpressionNode[] nodes, out Utf8Slice token)
    {
        token = default;
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind is ExpressionNodeKind.StringLiteral or ExpressionNodeKind.Identifier)
        {
            token = node.Token;
            return true;
        }

        return false;
    }

    private static bool IsPathMatch(ReadOnlySpan<PathSegment> actual, string[] expected, ReadOnlySpan<byte> expression)
    {
        if (actual.Length != expected.Length)
        {
            return false;
        }

        for (var i = 0; i < actual.Length; i++)
        {
            var expectedSegment = expected[i];
            var actualSegment = actual[i];
            if (expectedSegment == "*")
            {
                continue;
            }

            if (actualSegment.IsWildcard)
            {
                return false;
            }

            if (!TokenEqualsIgnoreCase(actualSegment.Token.AsSpan(expression), expectedSegment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TokenEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
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

    private static bool TokenEqualsIgnoreCase(ReadOnlySpan<byte> left, string right)
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

            if (r is >= 'A' and <= 'Z')
            {
                r = (char)(r + 32);
            }

            if (l != (byte)r)
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct PathSegment(Utf8Slice Token, bool IsWildcard);

    private static readonly string[][] s_untrustedPaths =
    [
        ["github", "event", "issue", "title"],
        ["github", "event", "issue", "body"],
        ["github", "event", "pull_request", "title"],
        ["github", "event", "pull_request", "body"],
        ["github", "event", "pull_request", "head", "ref"],
        ["github", "event", "pull_request", "head", "label"],
        ["github", "event", "pull_request", "head", "repo", "default_branch"],
        ["github", "event", "comment", "body"],
        ["github", "event", "review", "body"],
        ["github", "event", "review_comment", "body"],
        ["github", "event", "pages", "*", "page_name"],
        ["github", "event", "commits", "*", "message"],
        ["github", "event", "commits", "*", "author", "email"],
        ["github", "event", "commits", "*", "author", "name"],
        ["github", "event", "head_commit", "message"],
        ["github", "event", "head_commit", "author", "email"],
        ["github", "event", "head_commit", "author", "name"],
        ["github", "event", "discussion", "title"],
        ["github", "event", "discussion", "body"],
        ["github", "head_ref"],
    ];
}
