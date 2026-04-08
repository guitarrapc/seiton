using System.Text;
using Seiton.Core.Generated;

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
        var visitor = new SemanticValidationVisitor
        {
            ExpressionUtf8 = expressionUtf8,
            ExpressionLocation = expressionLocation,
            Context = context,
            Nodes = parseResult.Nodes,
            Arguments = parseResult.Arguments,
            Diagnostics = diagnostics,
            ValidatedRootIdentifiers = validatedRootIdentifiers,
        };

        ExpressionVisitor.VisitExprNode(
            parseResult.RootNode,
            parseResult.Nodes,
            parseResult.Arguments,
            ref visitor);

        return diagnostics.ToArray();
    }

    /// <summary>
    /// Zero-allocation visitor state for <see cref="Validate"/>.
    /// Declared as a <c>ref struct</c> so it can hold <see cref="ReadOnlySpan{byte}"/> as a field
    /// and implement <see cref="IExprNodeVisitor"/> (C# 13 / .NET 9+ allows ref structs to implement interfaces).
    /// Passed by <c>ref</c> to <see cref="ExpressionVisitor.VisitExprNode{TVisitor}"/> to avoid boxing.
    /// </summary>
    private ref struct SemanticValidationVisitor : IExprNodeVisitor
    {
        public ReadOnlySpan<byte> ExpressionUtf8;
        public TextRange ExpressionLocation;
        public ExpressionValidationContext Context;
        public ExpressionNode[] Nodes;
        public int[] Arguments;
        public List<Diagnostic> Diagnostics;
        public List<int> ValidatedRootIdentifiers;

        public void Visit(int nodeId, ExpressionNode node, int parentId, bool entering)
        {
            if (!entering)
            {
                return;
            }

            switch (node.Kind)
            {
                case ExpressionNodeKind.FunctionCall:
                    ValidateFunctionCall(node, Nodes, Arguments, ExpressionUtf8, ExpressionLocation, Diagnostics);
                    break;

                case ExpressionNodeKind.Identifier:
                {
                    // Skip the function name identifier — it is resolved via TryGetFunctionArity, not context root validation.
                    var isFunctionCallee = parentId >= 0
                        && parentId < Nodes.Length
                        && Nodes[parentId].Kind == ExpressionNodeKind.FunctionCall
                        && Nodes[parentId].Left == nodeId;
                    if (!isFunctionCallee)
                    {
                        ValidateContextRoot(nodeId, Nodes, ExpressionUtf8, ExpressionLocation, Context, Diagnostics, ValidatedRootIdentifiers);
                    }
                    break;
                }

                case ExpressionNodeKind.MemberAccess:
                case ExpressionNodeKind.WildcardAccess:
                case ExpressionNodeKind.IndexAccess:
                    ValidateContextRoot(nodeId, Nodes, ExpressionUtf8, ExpressionLocation, Context, Diagnostics, ValidatedRootIdentifiers);
                    break;
            }
        }
    }

    private static void ValidateFunctionCall(
        ExpressionNode functionCall,
        ExpressionNode[] nodes,
        int[] arguments,
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

        ValidateFunctionArgumentTypes(functionCall, nodes, arguments, expressionUtf8, functionName, expressionLocation, diagnostics);
    }

    private static void ValidateFunctionArgumentTypes(
        ExpressionNode functionCall,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        ReadOnlySpan<byte> functionName,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        if (SequenceEqualAsciiIgnoreCase(functionName, "contains"u8)
            || SequenceEqualAsciiIgnoreCase(functionName, "startsWith"u8)
            || SequenceEqualAsciiIgnoreCase(functionName, "endsWith"u8))
        {
            ValidateStringArg(functionCall, nodes, arguments, expressionUtf8, expressionLocation, diagnostics, 0, functionName);
            ValidateStringArg(functionCall, nodes, arguments, expressionUtf8, expressionLocation, diagnostics, 1, functionName);
            return;
        }

        if (SequenceEqualAsciiIgnoreCase(functionName, "format"u8)
            || SequenceEqualAsciiIgnoreCase(functionName, "fromJson"u8))
        {
            ValidateStringArg(functionCall, nodes, arguments, expressionUtf8, expressionLocation, diagnostics, 0, functionName);
            return;
        }

        if (SequenceEqualAsciiIgnoreCase(functionName, "join"u8))
        {
            // join(arrayOrString, separator?) where separator must be string when provided.
            ValidateStringArg(functionCall, nodes, arguments, expressionUtf8, expressionLocation, diagnostics, 1, functionName);
            return;
        }

        if (SequenceEqualAsciiIgnoreCase(functionName, "hashFiles"u8))
        {
            // hashFiles(path, path, ...) expects string globs.
            for (var i = 0; i < functionCall.ArgCount; i++)
            {
                ValidateStringArg(functionCall, nodes, arguments, expressionUtf8, expressionLocation, diagnostics, i, functionName);
            }
        }
    }

    private static void ValidateStringArg(
        ExpressionNode functionCall,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics,
        int argPosition,
        ReadOnlySpan<byte> functionName)
    {
        if (!TryGetArgumentNode(functionCall, nodes, arguments, argPosition, out var argumentNode))
        {
            return;
        }

        var argIndex = functionCall.ArgStart + argPosition;
        if (argIndex < 0 || argIndex >= arguments.Length)
        {
            return;
        }

        var argumentType = InferType(arguments[argIndex], nodes, arguments, expressionUtf8);
        if (argumentType is AnyExprType or StringExprType)
        {
            return;
        }

        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            $"function {Encoding.UTF8.GetString(functionName)} argument {argPosition + 1} should be string, but got {argumentType.TypeName}",
            ToNodeLocation(expressionLocation, argumentNode)));
    }

    private static bool TryGetArgumentNode(ExpressionNode functionCall, ExpressionNode[] nodes, int[] arguments, int argPosition, out ExpressionNode argumentNode)
    {
        argumentNode = default;
        if (argPosition < 0 || argPosition >= functionCall.ArgCount)
        {
            return false;
        }

        var argIndex = functionCall.ArgStart + argPosition;
        if (argIndex < 0 || argIndex >= arguments.Length)
        {
            return false;
        }

        var argNodeId = arguments[argIndex];
        if (argNodeId < 0 || argNodeId >= nodes.Length)
        {
            return false;
        }

        argumentNode = nodes[argNodeId];
        return true;
    }

    /// <summary>
    /// Infers the <see cref="ExprType"/> of the expression rooted at <paramref name="nodeId"/>.
    /// Performs a bottom-up traversal: the type of a node is derived from its children.
    /// Returns <see cref="ExprType.Any"/> for anything that cannot be statically determined.
    /// </summary>
    public static ExprType InferType(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return ExprType.Any;
        }

        var node = nodes[nodeId];
        return node.Kind switch
        {
            ExpressionNodeKind.StringLiteral => ExprType.String,
            ExpressionNodeKind.NumberLiteral => ExprType.Number,
            ExpressionNodeKind.BooleanLiteral => ExprType.Bool,
            ExpressionNodeKind.NullLiteral => ExprType.Null,
            ExpressionNodeKind.Unary => InferUnaryType(node, nodes, arguments, expressionUtf8),
            ExpressionNodeKind.Binary => InferBinaryType(node),
            ExpressionNodeKind.FunctionCall => InferFunctionReturnType(node, nodes, expressionUtf8),
            _ => ExprType.Any,
        };
    }

    private static ExprType InferUnaryType(ExpressionNode node, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expressionUtf8)
    {
        // Only unary operator remaining after arithmetic removal is `!`, which always yields bool.
        return ExprType.Bool;
    }

    private static ExprType InferBinaryType(ExpressionNode node)
    {
        return node.Operator switch
        {
            // Comparisons and logical operators always yield bool.
            ExpressionOperator.Equal
                or ExpressionOperator.NotEqual
                or ExpressionOperator.Less
                or ExpressionOperator.LessOrEqual
                or ExpressionOperator.Greater
                or ExpressionOperator.GreaterOrEqual
                or ExpressionOperator.And
                or ExpressionOperator.Or => ExprType.Bool,
            _ => ExprType.Any,
        };
    }

    private static ExprType InferFunctionReturnType(ExpressionNode functionCall, ExpressionNode[] nodes, ReadOnlySpan<byte> expressionUtf8)
    {
        if (functionCall.Left < 0 || functionCall.Left >= nodes.Length)
        {
            return ExprType.Any;
        }

        var callee = nodes[functionCall.Left];
        if (callee.Kind != ExpressionNodeKind.Identifier)
        {
            return ExprType.Any;
        }

        var name = callee.Token.AsSpan(expressionUtf8);

        // Bool-returning functions.
        if (SequenceEqualAsciiIgnoreCase(name, "contains"u8)
            || SequenceEqualAsciiIgnoreCase(name, "startsWith"u8)
            || SequenceEqualAsciiIgnoreCase(name, "endsWith"u8)
            || SequenceEqualAsciiIgnoreCase(name, "success"u8)
            || SequenceEqualAsciiIgnoreCase(name, "failure"u8)
            || SequenceEqualAsciiIgnoreCase(name, "always"u8)
            || SequenceEqualAsciiIgnoreCase(name, "cancelled"u8))
        {
            return ExprType.Bool;
        }

        // String-returning functions.
        if (SequenceEqualAsciiIgnoreCase(name, "format"u8)
            || SequenceEqualAsciiIgnoreCase(name, "join"u8)
            || SequenceEqualAsciiIgnoreCase(name, "toJson"u8)
            || SequenceEqualAsciiIgnoreCase(name, "hashFiles"u8))
        {
            return ExprType.String;
        }

        return ExprType.Any;
    }

    private static TextRange ToNodeLocation(TextRange expressionLocation, ExpressionNode node)
    {
        if (node.Token.Length <= 0)
        {
            return expressionLocation;
        }

        return ToLocation(expressionLocation, node.Token);
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
        return Availability.IsRootContextAvailable(context, rootName);
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
