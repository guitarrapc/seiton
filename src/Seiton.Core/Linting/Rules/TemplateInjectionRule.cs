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
        if (!TryBuildPathSegments(nodeId, nodes, expression, out var segments))
        {
            return false;
        }

        var candidates = new List<UntrustedInputNode>(4) { s_untrustedRoots };
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var next = new List<UntrustedInputNode>(4);
            for (var j = 0; j < candidates.Count; j++)
            {
                var node = candidates[j];
                if (node.TryGetChild(segment, out var direct))
                {
                    next.Add(direct);
                }

                if (node.TryGetChild("*", out var wildcard))
                {
                    next.Add(wildcard);
                }
            }

            if (next.Count == 0)
            {
                return false;
            }

            candidates = next;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].IsLeaf)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildPathSegments(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression, out List<string> segments)
    {
        segments = [];
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        switch (node.Kind)
        {
            case ExpressionNodeKind.Identifier:
                segments.Add(ToLowerAscii(node.Token.AsSpan(expression)));
                return true;
            case ExpressionNodeKind.MemberAccess:
                if (!TryBuildPathSegments(node.Left, nodes, expression, out segments))
                {
                    return false;
                }

                segments.Add(ToLowerAscii(node.Token.AsSpan(expression)));
                return true;
            case ExpressionNodeKind.WildcardAccess:
                if (!TryBuildPathSegments(node.Left, nodes, expression, out segments))
                {
                    return false;
                }

                segments.Add("*");
                return true;
            case ExpressionNodeKind.IndexAccess:
                if (!TryBuildPathSegments(node.Left, nodes, expression, out segments))
                {
                    return false;
                }

                if (TryGetIndexSegment(node.Right, nodes, expression, out var indexSegment))
                {
                    segments.Add(indexSegment);
                }
                else
                {
                    segments.Add("*");
                }

                return true;
            default:
                return false;
        }
    }

    private static bool TryGetIndexSegment(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression, out string segment)
    {
        segment = string.Empty;
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind is ExpressionNodeKind.StringLiteral or ExpressionNodeKind.Identifier)
        {
            segment = ToLowerAscii(node.Token.AsSpan(expression));
            return true;
        }

        return false;
    }

    private static string ToLowerAscii(ReadOnlySpan<byte> text)
    {
        var chars = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var b = text[i];
            if (b is >= (byte)'A' and <= (byte)'Z')
            {
                b = (byte)(b + 32);
            }

            chars[i] = (char)b;
        }

        return new string(chars);
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

    private sealed class UntrustedInputNode
    {
        private readonly Dictionary<string, UntrustedInputNode> _children = new(StringComparer.Ordinal);

        public bool IsLeaf { get; private set; }

        public void AddPath(ReadOnlySpan<string> segments)
        {
            if (segments.Length == 0)
            {
                IsLeaf = true;
                return;
            }

            var head = segments[0];
            if (!_children.TryGetValue(head, out var child))
            {
                child = new UntrustedInputNode();
                _children[head] = child;
            }

            child.AddPath(segments[1..]);
        }

        public bool TryGetChild(string key, out UntrustedInputNode child)
        {
            return _children.TryGetValue(key, out child!);
        }
    }

    private static UntrustedInputNode BuildUntrustedTree()
    {
        var root = new UntrustedInputNode();
        Add(root, "github", "event", "issue", "title");
        Add(root, "github", "event", "issue", "body");
        Add(root, "github", "event", "pull_request", "title");
        Add(root, "github", "event", "pull_request", "body");
        Add(root, "github", "event", "pull_request", "head", "ref");
        Add(root, "github", "event", "pull_request", "head", "label");
        Add(root, "github", "event", "pull_request", "head", "repo", "default_branch");
        Add(root, "github", "event", "comment", "body");
        Add(root, "github", "event", "review", "body");
        Add(root, "github", "event", "review_comment", "body");
        Add(root, "github", "event", "pages", "*", "page_name");
        Add(root, "github", "event", "commits", "*", "message");
        Add(root, "github", "event", "commits", "*", "author", "email");
        Add(root, "github", "event", "commits", "*", "author", "name");
        Add(root, "github", "event", "head_commit", "message");
        Add(root, "github", "event", "head_commit", "author", "email");
        Add(root, "github", "event", "head_commit", "author", "name");
        Add(root, "github", "event", "discussion", "title");
        Add(root, "github", "event", "discussion", "body");
        Add(root, "github", "head_ref");
        return root;
    }

    private static void Add(UntrustedInputNode root, params string[] segments)
    {
        root.AddPath(segments);
    }

    private static readonly UntrustedInputNode s_untrustedRoots = BuildUntrustedTree();
}
