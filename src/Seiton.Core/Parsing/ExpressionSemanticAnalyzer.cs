using System.Text;

namespace Seiton.Core.Parsing;

public enum ExpressionValidationContext
{
    Workflow,
    Job,
    Step,
}

public static class ExpressionSemanticAnalyzer
{
    public static Diagnostic[] Validate(
        ExpressionParseResult parseResult,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        ExpressionValidationContext context)
    {
        if (!parseResult.HasRoot)
        {
            return [];
        }

        var diagnostics = new List<Diagnostic>(4);
        var validatedRootIdentifiers = new List<int>(4);
        ValidateNode(
            parseResult.RootNode,
            parseResult.Nodes,
            parseResult.Arguments,
            expressionUtf8,
            expressionLocation,
            context,
            diagnostics,
            validatedRootIdentifiers,
            isFunctionCallee: false);
        return diagnostics.ToArray();
    }

    private static void ValidateNode(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        ExpressionValidationContext context,
        List<Diagnostic> diagnostics,
        List<int> validatedRootIdentifiers,
        bool isFunctionCallee)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return;
        }

        var node = nodes[nodeId];
        switch (node.Kind)
        {
            case ExpressionNodeKind.FunctionCall:
                ValidateFunctionCall(node, nodes, expressionUtf8, expressionLocation, diagnostics);
                for (var i = 0; i < node.ArgCount; i++)
                {
                    var argIndex = node.ArgStart + i;
                    if (argIndex >= 0 && argIndex < arguments.Length)
                    {
                        ValidateNode(
                            arguments[argIndex],
                            nodes,
                            arguments,
                            expressionUtf8,
                            expressionLocation,
                            context,
                            diagnostics,
                            validatedRootIdentifiers,
                            isFunctionCallee: false);
                    }
                }
                return;

            case ExpressionNodeKind.Identifier:
                if (!isFunctionCallee)
                {
                    ValidateContextRoot(
                        nodeId,
                        nodes,
                        expressionUtf8,
                        expressionLocation,
                        context,
                        diagnostics,
                        validatedRootIdentifiers);
                }
                return;

            case ExpressionNodeKind.MemberAccess:
            case ExpressionNodeKind.WildcardAccess:
            case ExpressionNodeKind.IndexAccess:
                ValidateContextRoot(
                    nodeId,
                    nodes,
                    expressionUtf8,
                    expressionLocation,
                    context,
                    diagnostics,
                    validatedRootIdentifiers);

                ValidateNode(
                    node.Left,
                    nodes,
                    arguments,
                    expressionUtf8,
                    expressionLocation,
                    context,
                    diagnostics,
                    validatedRootIdentifiers,
                    isFunctionCallee: false);

                if (node.Kind == ExpressionNodeKind.IndexAccess)
                {
                    ValidateNode(
                        node.Right,
                        nodes,
                        arguments,
                        expressionUtf8,
                        expressionLocation,
                        context,
                        diagnostics,
                        validatedRootIdentifiers,
                        isFunctionCallee: false);
                }
                return;

            case ExpressionNodeKind.Unary:
                ValidateNode(
                    node.Left,
                    nodes,
                    arguments,
                    expressionUtf8,
                    expressionLocation,
                    context,
                    diagnostics,
                    validatedRootIdentifiers,
                    isFunctionCallee: false);
                return;

            case ExpressionNodeKind.Binary:
                ValidateNode(
                    node.Left,
                    nodes,
                    arguments,
                    expressionUtf8,
                    expressionLocation,
                    context,
                    diagnostics,
                    validatedRootIdentifiers,
                    isFunctionCallee: false);
                ValidateNode(
                    node.Right,
                    nodes,
                    arguments,
                    expressionUtf8,
                    expressionLocation,
                    context,
                    diagnostics,
                    validatedRootIdentifiers,
                    isFunctionCallee: false);
                return;
        }
    }

    private static void ValidateFunctionCall(
        ExpressionNode functionCall,
        ExpressionNode[] nodes,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        if (functionCall.Left < 0 || functionCall.Left >= nodes.Length)
        {
            return;
        }

        var callee = nodes[functionCall.Left];
        if (callee.Kind != ExpressionNodeKind.Identifier)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "function call target must be identifier",
                expressionLocation));
            return;
        }

        var functionName = callee.Token.AsSpan(expressionUtf8);
        if (!TryGetFunctionArity(functionName, out var minArgs, out var maxArgs))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"unknown expression function: {Encoding.UTF8.GetString(functionName)}",
                ToLocation(expressionLocation, callee.Token)));
            return;
        }

        if (functionCall.ArgCount < minArgs || functionCall.ArgCount > maxArgs)
        {
            var message = maxArgs == int.MaxValue
                ? $"function {Encoding.UTF8.GetString(functionName)} expects at least {minArgs} argument(s), but got {functionCall.ArgCount}"
                : $"function {Encoding.UTF8.GetString(functionName)} expects {FormatExpectedArity(minArgs, maxArgs)} argument(s), but got {functionCall.ArgCount}";

            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                message,
                ToLocation(expressionLocation, callee.Token)));
        }
    }

    private static string FormatExpectedArity(int min, int max)
    {
        if (min == max)
        {
            return min.ToString();
        }

        return $"{min}-{max}";
    }

    private static void ValidateContextRoot(
        int nodeId,
        ExpressionNode[] nodes,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        ExpressionValidationContext context,
        List<Diagnostic> diagnostics,
        List<int> validatedRootIdentifiers)
    {
        if (!TryGetRootIdentifier(nodeId, nodes, out var rootIdentifierNodeId, out var rootToken))
        {
            return;
        }

        for (var i = 0; i < validatedRootIdentifiers.Count; i++)
        {
            if (validatedRootIdentifiers[i] == rootIdentifierNodeId)
            {
                return;
            }
        }

        validatedRootIdentifiers.Add(rootIdentifierNodeId);
        var rootName = rootToken.AsSpan(expressionUtf8);
        if (IsRootAvailableInContext(rootName, context))
        {
            return;
        }

        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            $"context '{Encoding.UTF8.GetString(rootName)}' is not available in {ContextName(context)} expressions",
            ToLocation(expressionLocation, rootToken)));
    }

    private static bool TryGetRootIdentifier(int nodeId, ExpressionNode[] nodes, out int rootNodeId, out Utf8Slice token)
    {
        rootNodeId = -1;
        token = default;

        var currentNodeId = nodeId;
        while (currentNodeId >= 0 && currentNodeId < nodes.Length)
        {
            var node = nodes[currentNodeId];
            switch (node.Kind)
            {
                case ExpressionNodeKind.Identifier:
                    rootNodeId = currentNodeId;
                    token = node.Token;
                    return true;

                case ExpressionNodeKind.MemberAccess:
                case ExpressionNodeKind.WildcardAccess:
                case ExpressionNodeKind.IndexAccess:
                    currentNodeId = node.Left;
                    continue;

                default:
                    return false;
            }
        }

        return false;
    }

    private static string ContextName(ExpressionValidationContext context)
    {
        return context switch
        {
            ExpressionValidationContext.Workflow => "workflow",
            ExpressionValidationContext.Job => "job",
            ExpressionValidationContext.Step => "step",
            _ => "unknown",
        };
    }

    private static bool IsRootAvailableInContext(ReadOnlySpan<byte> rootName, ExpressionValidationContext context)
    {
        if (rootName.SequenceEqual("github"u8) || rootName.SequenceEqual("inputs"u8) || rootName.SequenceEqual("vars"u8))
        {
            return true;
        }

        if (context is ExpressionValidationContext.Job or ExpressionValidationContext.Step)
        {
            if (rootName.SequenceEqual("needs"u8) || rootName.SequenceEqual("strategy"u8) || rootName.SequenceEqual("matrix"u8))
            {
                return true;
            }
        }

        if (context == ExpressionValidationContext.Step)
        {
            return rootName.SequenceEqual("job"u8)
                || rootName.SequenceEqual("runner"u8)
                || rootName.SequenceEqual("env"u8)
                || rootName.SequenceEqual("secrets"u8)
                || rootName.SequenceEqual("steps"u8);
        }

        return false;
    }

    private static bool TryGetFunctionArity(ReadOnlySpan<byte> functionName, out int minArgs, out int maxArgs)
    {
        if (SequenceEqualAsciiIgnoreCase(functionName, "contains"u8)
            || SequenceEqualAsciiIgnoreCase(functionName, "startsWith"u8)
            || SequenceEqualAsciiIgnoreCase(functionName, "endsWith"u8))
        {
            minArgs = 2;
            maxArgs = 2;
            return true;
        }

        if (SequenceEqualAsciiIgnoreCase(functionName, "format"u8))
        {
            minArgs = 1;
            maxArgs = int.MaxValue;
            return true;
        }

        if (SequenceEqualAsciiIgnoreCase(functionName, "join"u8))
        {
            minArgs = 1;
            maxArgs = 2;
            return true;
        }

        if (SequenceEqualAsciiIgnoreCase(functionName, "toJson"u8)
            || SequenceEqualAsciiIgnoreCase(functionName, "fromJson"u8))
        {
            minArgs = 1;
            maxArgs = 1;
            return true;
        }

        if (SequenceEqualAsciiIgnoreCase(functionName, "success"u8)
            || SequenceEqualAsciiIgnoreCase(functionName, "failure"u8)
            || SequenceEqualAsciiIgnoreCase(functionName, "cancelled"u8)
            || SequenceEqualAsciiIgnoreCase(functionName, "always"u8))
        {
            minArgs = 0;
            maxArgs = 0;
            return true;
        }

        if (SequenceEqualAsciiIgnoreCase(functionName, "hashFiles"u8))
        {
            minArgs = 1;
            maxArgs = int.MaxValue;
            return true;
        }

        minArgs = 0;
        maxArgs = 0;
        return false;
    }

    private static bool SequenceEqualAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = right[i];
            if (ToLowerAscii(l) != ToLowerAscii(r))
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToLowerAscii(byte value)
    {
        if (value is >= (byte)'A' and <= (byte)'Z')
        {
            return (byte)(value + 32);
        }

        return value;
    }

    private static TextRange ToLocation(TextRange expressionLocation, Utf8Slice token)
    {
        if (token.Length <= 0)
        {
            return expressionLocation;
        }

        var start = expressionLocation.Start + token.Offset;
        var startColumn = expressionLocation.StartColumn + token.Offset;
        var endColumn = startColumn + token.Length - 1;
        return new TextRange(
            Start: start,
            Length: token.Length,
            StartLine: expressionLocation.StartLine,
            StartColumn: startColumn,
            EndLine: expressionLocation.StartLine,
            EndColumn: endColumn);
    }
}
