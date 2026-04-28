using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Detects expressions in <c>run:</c> scripts that may be vulnerable to template injection attacks.</summary>
public sealed class TemplateInjectionRule() : RuleBase(RuleId.TemplateInjection)
{
    private static readonly string[][] untrustedPaths =
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
        else if (step.Exec is ExecAction action)
        {
            CheckActionScriptSink(step, action);
        }
    }

    private void CheckActionScriptSink(Step step, ExecAction action)
    {
        if (!action.Uses.HasValue || action.Inputs is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var uses = Arena.GetStringValue(action.Uses);
        if (!IsGithubScriptAction(uses))
        {
            return;
        }

        foreach (var pair in action.Inputs)
        {
            var keySpan = pair.Key.AsSpan(Config.Utf8Yaml);
            if (keySpan.SequenceEqual("script"u8))
            {
                CheckSink(step, pair.Value, "script");
                return;
            }
        }
    }

    private static bool IsGithubScriptAction(ReadOnlySpan<byte> uses)
    {
        // Match actions/github-script@<any version>
        const byte AtSign = (byte)'@';
        var atIndex = uses.IndexOf(AtSign);
        if (atIndex < 0)
        {
            return false;
        }

        return uses[..atIndex].SequenceEqual("actions/github-script"u8);
    }

    private void CheckSink(Step step, StringNodeId valueNode, string sinkName)
    {
        if (!valueNode.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var value = Arena.GetStringValue(valueNode);
        var valueSlice = Arena.GetStringSlice(valueNode);
        var lineStarts = Config.GetLineStarts();
        var searchStart = 0;
        while (TryFindExpression(value, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;

            var expression = TrimAsciiWhiteSpace(value.Slice(bodyStart, bodyLength));
            if (expression.Length == 0)
            {
                continue;
            }

            // Compute trim offset: how many bytes were trimmed from the left
            var rawExpression = value.Slice(bodyStart, bodyLength);
            var trimOffset = 0;
            while (trimOffset < rawExpression.Length && IsAsciiWhiteSpace(rawExpression[trimOffset]))
            {
                trimOffset++;
            }

            var parseResult = Config.ParseExpression(expression);
            if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
            {
                continue;
            }

            ReportUntrustedReferences(step, parseResult, expression, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
        }
    }

    private void ReportUntrustedReferences(
        Step step,
        ExpressionParseResult parseResult,
        ReadOnlySpan<byte> expression,
        Utf8Slice valueSlice,
        int bodyStart,
        int trimOffset,
        int[] lineStarts,
        string sinkName)
    {
        CollectUntrustedReferences(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression, safeDepth: 0,
            step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
    }

    private void CollectUntrustedReferences(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        int safeDepth,
        Step step,
        Utf8Slice valueSlice,
        int bodyStart,
        int trimOffset,
        int[] lineStarts,
        string sinkName)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return;
        }

        if (safeDepth == 0 && IsUntrustedReference(nodeId, nodes, expression))
        {
            EmitUntrustedDiagnostic(step, nodeId, nodes, expression, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
            // Also check index expressions within this path for nested untrusted references
            CollectNestedIndexReferences(nodeId, nodes, arguments, expression, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
            return;
        }

        var node = nodes[nodeId];
        switch (node.Kind)
        {
            case ExpressionNodeKind.Unary:
                CollectUntrustedReferences(node.Left, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
                break;
            case ExpressionNodeKind.Binary:
                CollectUntrustedReferences(node.Left, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
                CollectUntrustedReferences(node.Right, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
                break;
            case ExpressionNodeKind.MemberAccess:
                CollectUntrustedReferences(node.Left, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
                break;
            case ExpressionNodeKind.WildcardAccess:
                CollectUntrustedReferences(node.Left, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
                break;
            case ExpressionNodeKind.IndexAccess:
                CollectUntrustedReferences(node.Left, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
                CollectUntrustedReferences(node.Right, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
                break;
            case ExpressionNodeKind.FunctionCall:
                CollectUntrustedReferencesInFunction(node, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
                break;
        }
    }

    private void CollectUntrustedReferencesInFunction(
        ExpressionNode functionCallNode,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        int safeDepth,
        Step step,
        Utf8Slice valueSlice,
        int bodyStart,
        int trimOffset,
        int[] lineStarts,
        string sinkName)
    {
        var calleeSafeDepth = safeDepth;
        if (IsSafeFunctionCall(functionCallNode, nodes, expression))
        {
            calleeSafeDepth++;
        }

        CollectUntrustedReferences(functionCallNode.Left, nodes, arguments, expression, safeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);

        for (var i = 0; i < functionCallNode.ArgCount; i++)
        {
            var argIndex = functionCallNode.ArgStart + i;
            if (argIndex < 0 || argIndex >= arguments.Length)
            {
                continue;
            }

            CollectUntrustedReferences(arguments[argIndex], nodes, arguments, expression, calleeSafeDepth, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
        }
    }

    /// <summary>Walk a matched untrusted path tree and check IndexAccess right-side sub-expressions for nested untrusted references.</summary>
    private void CollectNestedIndexReferences(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        Step step,
        Utf8Slice valueSlice,
        int bodyStart,
        int trimOffset,
        int[] lineStarts,
        string sinkName)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return;
        }

        var node = nodes[nodeId];
        switch (node.Kind)
        {
            case ExpressionNodeKind.MemberAccess:
            case ExpressionNodeKind.WildcardAccess:
                CollectNestedIndexReferences(node.Left, nodes, arguments, expression, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
                break;
            case ExpressionNodeKind.IndexAccess:
                CollectNestedIndexReferences(node.Left, nodes, arguments, expression, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
                CollectUntrustedReferences(node.Right, nodes, arguments, expression, safeDepth: 0, step, valueSlice, bodyStart, trimOffset, lineStarts, sinkName);
                break;
        }
    }

    private void EmitUntrustedDiagnostic(
        Step step,
        int nodeId,
        ExpressionNode[] nodes,
        ReadOnlySpan<byte> expression,
        Utf8Slice valueSlice,
        int bodyStart,
        int trimOffset,
        int[] lineStarts,
        string sinkName)
    {
        // Build the dotted path string for the untrusted reference
        Span<PathSegment> segments = stackalloc PathSegment[16];
        if (!TryBuildPathSegments(nodeId, nodes, expression, segments, out var segCount))
        {
            return;
        }

        var pathString = BuildPathString(segments[..segCount], expression);

        // Find the root identifier token offset (leftmost identifier in the chain)
        var rootTokenOffset = FindRootIdentifierOffset(nodeId, nodes);

        // Compute precise position: absolute offset in UTF-8 YAML
        var absoluteStart = valueSlice.Offset + bodyStart + trimOffset + rootTokenOffset;
        var start = OffsetToLineColumn(lineStarts, absoluteStart);

        // End position spans the entire path expression
        var lastNode = nodes[nodeId];
        var endOffset = lastNode.Token.Offset + lastNode.Token.Length;
        var absoluteEnd = valueSlice.Offset + bodyStart + trimOffset + endOffset;
        var end = OffsetToLineColumn(lineStarts, absoluteEnd - 1);

        var location = new TextRange(
            Start: absoluteStart,
            Length: absoluteEnd - absoluteStart,
            StartLine: start.Line,
            StartColumn: start.Column,
            EndLine: end.Line,
            EndColumn: end.Column);

        var message = $"\"{pathString}\" is potentially untrusted. avoid using it directly in inline scripts. instead, pass it through an environment variable. see https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions#good-practices-for-mitigating-script-injection-attacks for more details";
        AddStepError(step, message, location);
    }

    private static int FindRootIdentifierOffset(int nodeId, ExpressionNode[] nodes)
    {
        var current = nodeId;
        while (current >= 0 && current < nodes.Length)
        {
            var node = nodes[current];
            if (node.Kind == ExpressionNodeKind.Identifier)
            {
                return node.Token.Offset;
            }

            if (node.Kind is ExpressionNodeKind.MemberAccess or ExpressionNodeKind.WildcardAccess or ExpressionNodeKind.IndexAccess)
            {
                current = node.Left;
            }
            else
            {
                break;
            }
        }

        return 0;
    }

    private static string BuildPathString(ReadOnlySpan<PathSegment> segments, ReadOnlySpan<byte> expression)
    {
        var sb = new System.Text.StringBuilder(64);
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('.');
            }

            if (segments[i].IsWildcard)
            {
                sb.Append('*');
            }
            else
            {
                var span = segments[i].Token.AsSpan(expression);
                for (var j = 0; j < span.Length; j++)
                {
                    sb.Append((char)span[j]);
                }
            }
        }

        return sb.ToString();
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

        for (var i = 0; i < untrustedPaths.Length; i++)
        {
            if (IsPathMatch(segments[..count], untrustedPaths[i], expression))
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

            // Expected wildcard matches any actual segment
            if (expectedSegment == "*")
            {
                continue;
            }

            // Actual wildcard (e.g., github.event.*.body) matches any expected segment
            if (actualSegment.IsWildcard)
            {
                continue;
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
}
