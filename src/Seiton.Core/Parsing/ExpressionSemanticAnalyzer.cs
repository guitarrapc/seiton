using System.Text;
using System.Text.Json;
using Seiton.Core.Generated;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;

namespace Seiton.Core.Parsing;

public enum ExpressionValidationContext
{
    Workflow,
    WorkflowCallOutput,
    Job,
    JobOutput,
    ReusableWorkflowCallSecrets,
    Step,
}

public static class ExpressionSemanticAnalyzer
{
    internal readonly record struct FuncOverload(ExprType ReturnType, ExprType[] Parameters, ExprType? VariadicParameter = null)
    {
        public int MinArgs => Parameters.Length;

        public int MaxArgs => VariadicParameter is null ? Parameters.Length : int.MaxValue;

        public bool AcceptsArgCount(int argCount)
        {
            return argCount >= MinArgs && argCount <= MaxArgs;
        }

        public ExprType GetExpectedTypeAt(int index)
        {
            if (index < Parameters.Length)
            {
                return Parameters[index];
            }

            return VariadicParameter ?? ExprType.Any;
        }
    }

    internal readonly record struct FunctionSpec(byte[] NameUtf8, FuncOverload[] Overloads);

    private static readonly FunctionSpec[] Specs = Generated.FunctionSpecs.Specs;

    private static readonly (byte[] NameUtf8, ExprType Type)[] BuiltinContextTypes = Generated.ContextTypes.BuiltinContextTypes;

    private static bool TryGetBuiltinContextType(ReadOnlySpan<byte> nameUtf8, out ExprType type)
    {
        for (var i = 0; i < BuiltinContextTypes.Length; i++)
        {
            if (EqualsAsciiIgnoreCase(nameUtf8, BuiltinContextTypes[i].NameUtf8))
            {
                type = BuiltinContextTypes[i].Type;
                return true;
            }
        }

        type = ExprType.Any;
        return false;
    }

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

        var diagnostics = new List<Diagnostic>();
        ValidateNode(parseResult.RootNode, -1, parseResult.Nodes, parseResult.Arguments, expressionUtf8, expressionLocation, context, diagnostics);
        return diagnostics.ToArray();
    }

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
            ExpressionNodeKind.Identifier => InferIdentifierType(node, expressionUtf8),
            ExpressionNodeKind.Unary => node.Operator == ExpressionOperator.Not
                ? ExprType.Bool
                : ExprType.Any,
            ExpressionNodeKind.Binary => InferBinaryType(node),
            ExpressionNodeKind.MemberAccess => InferMemberAccessType(node, nodes, arguments, expressionUtf8),
            ExpressionNodeKind.IndexAccess => InferIndexAccessType(node, nodes, arguments, expressionUtf8),
            ExpressionNodeKind.WildcardAccess => InferWildcardType(node, nodes, arguments, expressionUtf8),
            ExpressionNodeKind.FunctionCall => InferFunctionCallType(node, nodes, arguments, expressionUtf8),
            _ => ExprType.Any,
        };
    }

    public static bool TryGetFunctionArity(ReadOnlySpan<byte> nameUtf8, out int minArgs, out int maxArgs)
    {
        if (TryGetFunctionSpec(nameUtf8, out var spec))
        {
            minArgs = int.MaxValue;
            maxArgs = 0;
            for (var i = 0; i < spec.Overloads.Length; i++)
            {
                var overload = spec.Overloads[i];
                if (overload.MinArgs < minArgs)
                {
                    minArgs = overload.MinArgs;
                }

                if (overload.MaxArgs > maxArgs)
                {
                    maxArgs = overload.MaxArgs;
                }
            }

            if (minArgs == int.MaxValue)
            {
                minArgs = 0;
            }

            return true;
        }

        minArgs = 0;
        maxArgs = 0;
        return false;
    }

    private static void ValidateNode(
        int nodeId,
        int parentId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        ExpressionValidationContext context,
        List<Diagnostic> diagnostics)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return;
        }

        var node = nodes[nodeId];

        if (node.Kind == ExpressionNodeKind.Identifier && IsContextRootIdentifier(nodeId, parentId, nodes))
        {
            var rootName = node.Token.AsSpan(expressionUtf8);
            if (!TryGetBuiltinContextType(rootName, out _))
            {
                var rootNameText = Encoding.UTF8.GetString(rootName);
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"undefined context '{rootNameText}'",
                    expressionLocation));
            }
            else if (!Availability.IsRootContextAvailable(context, rootName))
            {
                var rootNameText = Encoding.UTF8.GetString(rootName);
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"context '{rootNameText}' is not available in {ToContextText(context)} expressions",
                    expressionLocation));
            }
        }

        if (node.Kind == ExpressionNodeKind.FunctionCall)
        {
            ValidateFunctionCall(node, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
        }

        switch (node.Kind)
        {
            case ExpressionNodeKind.Unary:
                ValidateNode(node.Left, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, diagnostics);
                ValidateUnaryOp(node, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
                break;
            case ExpressionNodeKind.Binary:
                ValidateNode(node.Left, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, diagnostics);
                ValidateNode(node.Right, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, diagnostics);
                ValidateCompareOp(node, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
                break;
            case ExpressionNodeKind.MemberAccess:
                ValidateNode(node.Left, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, diagnostics);
                ValidatePropertyAccess(node, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
                break;
            case ExpressionNodeKind.WildcardAccess:
                ValidateNode(node.Left, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, diagnostics);
                ValidateWildcardAccess(node, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
                break;
            case ExpressionNodeKind.IndexAccess:
                ValidateNode(node.Left, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, diagnostics);
                ValidateNode(node.Right, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, diagnostics);
                ValidateIndexAccess(node, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
                break;
            case ExpressionNodeKind.FunctionCall:
                ValidateNode(node.Left, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, diagnostics);
                for (var i = 0; i < node.ArgCount; i++)
                {
                    var argIndex = node.ArgStart + i;
                    if (argIndex >= 0 && argIndex < arguments.Length)
                    {
                        ValidateNode(arguments[argIndex], nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, diagnostics);
                    }
                }
                break;
        }
    }

    private static void ValidateFunctionCall(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        if (node.Left < 0 || node.Left >= nodes.Length)
        {
            return;
        }

        var callee = nodes[node.Left];
        if (callee.Kind != ExpressionNodeKind.Identifier)
        {
            return;
        }

        var nameUtf8 = callee.Token.AsSpan(expressionUtf8);
        if (!TryGetFunctionSpec(nameUtf8, out var spec))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"unknown expression function: {Encoding.UTF8.GetString(nameUtf8)}",
                expressionLocation));
            return;
        }

        var argCount = node.ArgCount;
        var countMatches = false;
        for (var i = 0; i < spec.Overloads.Length; i++)
        {
            if (spec.Overloads[i].AcceptsArgCount(argCount))
            {
                countMatches = true;
                break;
            }
        }

        if (!countMatches)
        {
            var (minArgs, maxArgs) = GetArityRange(spec);
            var message = maxArgs == minArgs
                ? $"function '{Encoding.UTF8.GetString(nameUtf8)}' expects {minArgs} argument(s), but got {argCount}"
                : $"function '{Encoding.UTF8.GetString(nameUtf8)}' expects {minArgs}-{maxArgs} argument(s), but got {argCount}";
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, expressionLocation));
            return;
        }

        var argTypes = new ExprType[argCount];
        for (var i = 0; i < argCount; i++)
        {
            var argIndex = node.ArgStart + i;
            if (argIndex >= 0 && argIndex < arguments.Length)
            {
                argTypes[i] = InferType(arguments[argIndex], nodes, arguments, expressionUtf8);
            }
            else
            {
                argTypes[i] = ExprType.Any;
            }
        }

        var typeMatched = false;
        for (var i = 0; i < spec.Overloads.Length; i++)
        {
            var overload = spec.Overloads[i];
            if (!overload.AcceptsArgCount(argCount))
            {
                continue;
            }

            if (TryValidateAgainstOverload(overload, argTypes, out _, out _, out _))
            {
                typeMatched = true;
                break;
            }
        }

        if (typeMatched)
        {
            ValidateFormatPlaceholders(node, nameUtf8, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
            return;
        }

        for (var i = 0; i < spec.Overloads.Length; i++)
        {
            var overload = spec.Overloads[i];
            if (!overload.AcceptsArgCount(argCount))
            {
                continue;
            }

            if (TryValidateAgainstOverload(overload, argTypes, out var errorArgIndex, out var expectedType, out var actualType))
            {
                return;
            }

            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"argument {errorArgIndex + 1} should be {expectedType.TypeName}, but got {actualType.TypeName}",
                expressionLocation));
            return;
        }

        ValidateFormatPlaceholders(node, nameUtf8, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
    }

    private static void ValidateFormatPlaceholders(
        ExpressionNode functionCallNode,
        ReadOnlySpan<byte> functionNameUtf8,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        if (!EqualsAsciiIgnoreCase(functionNameUtf8, "format"u8))
        {
            return;
        }

        if (functionCallNode.ArgCount == 0 || functionCallNode.ArgStart < 0 || functionCallNode.ArgStart >= arguments.Length)
        {
            return;
        }

        var templateNodeId = arguments[functionCallNode.ArgStart];
        if (templateNodeId < 0 || templateNodeId >= nodes.Length)
        {
            return;
        }

        var templateNode = nodes[templateNodeId];
        if (templateNode.Kind != ExpressionNodeKind.StringLiteral)
        {
            return;
        }

        var template = templateNode.Token.AsSpan(expressionUtf8);
        var formatArgCount = functionCallNode.ArgCount - 1;

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != (byte)'{')
            {
                continue;
            }

            if (i + 1 < template.Length && template[i + 1] == (byte)'{')
            {
                i++;
                continue;
            }

            var j = i + 1;
            var hasDigits = false;
            var indexValue = 0;
            while (j < template.Length && template[j] is >= (byte)'0' and <= (byte)'9')
            {
                hasDigits = true;
                indexValue = (indexValue * 10) + (template[j] - (byte)'0');
                j++;
            }

            if (!hasDigits)
            {
                continue;
            }

            if (j >= template.Length)
            {
                continue;
            }

            while (j < template.Length && template[j] != (byte)'}')
            {
                j++;
            }

            if (j >= template.Length)
            {
                continue;
            }

            if (indexValue >= formatArgCount)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"format placeholder '{{{indexValue}}}' requires argument {indexValue + 1}, but got {formatArgCount} format argument(s)",
                    expressionLocation));
                return;
            }

            i = j;
        }
    }

    private static bool TryValidateAgainstOverload(FuncOverload overload, ExprType[] argTypes, out int errorArgIndex, out ExprType expected, out ExprType actual)
    {
        for (var i = 0; i < argTypes.Length; i++)
        {
            var expectedType = overload.GetExpectedTypeAt(i);
            var actualType = argTypes[i];
            if (!actualType.IsAssignableTo(expectedType))
            {
                errorArgIndex = i;
                expected = expectedType;
                actual = actualType;
                return false;
            }
        }

        errorArgIndex = -1;
        expected = ExprType.Any;
        actual = ExprType.Any;
        return true;
    }

    private static (int MinArgs, int MaxArgs) GetArityRange(FunctionSpec spec)
    {
        var minArgs = int.MaxValue;
        var maxArgs = 0;
        for (var i = 0; i < spec.Overloads.Length; i++)
        {
            var overload = spec.Overloads[i];
            if (overload.MinArgs < minArgs)
            {
                minArgs = overload.MinArgs;
            }

            if (overload.MaxArgs > maxArgs)
            {
                maxArgs = overload.MaxArgs;
            }
        }

        if (minArgs == int.MaxValue)
        {
            minArgs = 0;
        }

        return (minArgs, maxArgs);
    }

    private static ExprType InferBinaryType(ExpressionNode node)
    {
        return node.Operator switch
        {
            ExpressionOperator.And
                or ExpressionOperator.Or
                or ExpressionOperator.Equal
                or ExpressionOperator.NotEqual
                or ExpressionOperator.Less
                or ExpressionOperator.LessOrEqual
                or ExpressionOperator.Greater
                or ExpressionOperator.GreaterOrEqual => ExprType.Bool,
            _ => ExprType.Any,
        };
    }

    private static ExprType InferMemberAccessType(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8)
    {
        var leftType = InferType(node.Left, nodes, arguments, expressionUtf8);
        if (leftType is ObjectExprType objectType)
        {
            if (objectType.TryGetProperty(node.Token.AsSpan(expressionUtf8), out var propertyType))
            {
                return propertyType;
            }

            return ExprType.Any;
        }

        if (leftType is ArrayExprType arrayType)
        {
            if (node.Token.AsSpan(expressionUtf8).SequenceEqual("*"u8))
            {
                return arrayType.ElementType;
            }
        }

        return ExprType.Any;
    }

    private static ExprType InferIndexAccessType(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8)
    {
        var leftType = InferType(node.Left, nodes, arguments, expressionUtf8);
        var rightType = InferType(node.Right, nodes, arguments, expressionUtf8);

        if (leftType is ArrayExprType arrayType)
        {
            if (rightType.IsAssignableTo(ExprType.Number) || rightType is AnyExprType)
            {
                return arrayType.ElementType;
            }

            return ExprType.Any;
        }

        if (leftType is ObjectExprType objectType)
        {
            if (node.Right >= 0
                && node.Right < nodes.Length
                && nodes[node.Right].Kind == ExpressionNodeKind.StringLiteral)
            {
                var propertyName = nodes[node.Right].Token.AsSpan(expressionUtf8);
                if (objectType.TryGetProperty(propertyName, out var propertyType))
                {
                    return propertyType;
                }
            }

            if (rightType.IsAssignableTo(ExprType.String) || rightType is AnyExprType)
            {
                return objectType.DynamicPropertyType ?? ExprType.Any;
            }
        }

        return ExprType.Any;
    }

    private static ExprType InferWildcardType(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8)
    {
        var leftType = InferType(node.Left, nodes, arguments, expressionUtf8);
        if (leftType is ArrayExprType arrayType)
        {
            return arrayType.ElementType;
        }

        if (leftType is ObjectExprType objectType)
        {
            return objectType.DynamicPropertyType ?? ExprType.Any;
        }

        return ExprType.Any;
    }

    private static ExprType InferFunctionCallType(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8)
    {
        if (node.Left < 0 || node.Left >= nodes.Length)
        {
            return ExprType.Any;
        }

        var callee = nodes[node.Left];
        if (callee.Kind != ExpressionNodeKind.Identifier)
        {
            return ExprType.Any;
        }

        var nameUtf8 = callee.Token.AsSpan(expressionUtf8);
        if (!TryGetFunctionSpec(nameUtf8, out var spec))
        {
            return ExprType.Any;
        }

        if (EqualsAsciiIgnoreCase(nameUtf8, "fromjson"u8)
            && node.ArgCount == 1
            && node.ArgStart >= 0
            && node.ArgStart < arguments.Length)
        {
            var argNodeIndex = arguments[node.ArgStart];
            var literalType = TryInferFromJsonLiteral(argNodeIndex, nodes, expressionUtf8);
            if (literalType is not null)
            {
                return literalType;
            }
        }

        for (var i = 0; i < spec.Overloads.Length; i++)
        {
            if (spec.Overloads[i].AcceptsArgCount(node.ArgCount))
            {
                return spec.Overloads[i].ReturnType;
            }
        }

        return ExprType.Any;
    }

    private static ExprType? TryInferFromJsonLiteral(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expressionUtf8)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return null;
        }

        var node = nodes[nodeId];
        if (node.Kind != ExpressionNodeKind.StringLiteral)
        {
            return null;
        }

        var jsonText = Encoding.UTF8.GetString(node.Token.AsSpan(expressionUtf8));
        try
        {
            using var document = JsonDocument.Parse(jsonText);
            return ConvertJsonType(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static ExprType ConvertJsonType(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var properties = new Dictionary<Utf8String, ExprType>();
                    foreach (var property in element.EnumerateObject())
                    {
                        properties[new Utf8String(Encoding.UTF8.GetBytes(property.Name))] = ConvertJsonType(property.Value);
                    }

                    return ExprType.Object(properties, dynamicPropertyType: ExprType.Any, strict: false);
                }
            case JsonValueKind.Array:
                {
                    ExprType? elementType = null;
                    foreach (var child in element.EnumerateArray())
                    {
                        var current = ConvertJsonType(child);
                        if (elementType is null)
                        {
                            elementType = current;
                        }
                        else if (!current.IsAssignableTo(elementType) || !elementType.IsAssignableTo(current))
                        {
                            elementType = ExprType.Any;
                            break;
                        }
                    }

                    return ExprType.ArrayOf(elementType ?? ExprType.Any);
                }
            case JsonValueKind.String:
                return ExprType.String;
            case JsonValueKind.Number:
                return ExprType.Number;
            case JsonValueKind.True:
            case JsonValueKind.False:
                return ExprType.Bool;
            case JsonValueKind.Null:
                return ExprType.Null;
            default:
                return ExprType.Any;
        }
    }

    private static bool TryGetFunctionSpec(ReadOnlySpan<byte> nameUtf8, out FunctionSpec spec)
    {
        for (var i = 0; i < Specs.Length; i++)
        {
            if (EqualsAsciiIgnoreCase(nameUtf8, Specs[i].NameUtf8))
            {
                spec = Specs[i];
                return true;
            }
        }

        spec = default;
        return false;
    }

    private static ExprType InferIdentifierType(ExpressionNode node, ReadOnlySpan<byte> expressionUtf8)
    {
        var name = node.Token.AsSpan(expressionUtf8);
        if (TryGetBuiltinContextType(name, out var type))
        {
            return type;
        }

        return ExprType.Any;
    }

    private static bool IsComparisonOperator(ExpressionOperator op)
    {
        return op is ExpressionOperator.Less
            or ExpressionOperator.LessOrEqual
            or ExpressionOperator.Greater
            or ExpressionOperator.GreaterOrEqual;
    }

    private static bool IsNotComparableType(ExprType type)
    {
        return type is NullExprType or BoolExprType or ObjectExprType or ArrayExprType;
    }

    private static void ValidateCompareOp(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        if (!IsComparisonOperator(node.Operator))
        {
            return;
        }

        var leftType = InferType(node.Left, nodes, arguments, expressionUtf8);
        var rightType = InferType(node.Right, nodes, arguments, expressionUtf8);

        if (IsNotComparableType(leftType))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"operator '{OperatorSymbol(node.Operator)}' does not support {leftType.TypeName} type",
                expressionLocation));
        }
        else if (IsNotComparableType(rightType))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"operator '{OperatorSymbol(node.Operator)}' does not support {rightType.TypeName} type",
                expressionLocation));
        }
    }

    private static void ValidateUnaryOp(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        if (node.Operator != ExpressionOperator.Not)
        {
            return;
        }

        var operandType = InferType(node.Left, nodes, arguments, expressionUtf8);
        if (operandType is ObjectExprType or ArrayExprType)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"operator '!' does not support {operandType.TypeName} type",
                expressionLocation));
        }
    }

    private static void ValidateWildcardAccess(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        var leftType = InferType(node.Left, nodes, arguments, expressionUtf8);
        if (leftType is AnyExprType or ObjectExprType or ArrayExprType)
        {
            return;
        }

        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            $"receiver of '.*' must be an object or array, but got {leftType.TypeName}",
            expressionLocation));
    }

    private static void ValidateIndexAccess(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        var leftType = InferType(node.Left, nodes, arguments, expressionUtf8);
        var rightType = InferType(node.Right, nodes, arguments, expressionUtf8);

        if (leftType is ArrayExprType && rightType is not (AnyExprType or NumberExprType))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"index of array must be number, but got {rightType.TypeName}",
                expressionLocation));
        }
        else if (leftType is ObjectExprType && rightType is not (AnyExprType or StringExprType))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"index of object must be string, but got {rightType.TypeName}",
                expressionLocation));
        }
    }

    private static string OperatorSymbol(ExpressionOperator op)
    {
        return op switch
        {
            ExpressionOperator.Less => "<",
            ExpressionOperator.LessOrEqual => "<=",
            ExpressionOperator.Greater => ">",
            ExpressionOperator.GreaterOrEqual => ">=",
            _ => op.ToString(),
        };
    }

    private static void ValidatePropertyAccess(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        var leftType = InferType(node.Left, nodes, arguments, expressionUtf8);
        if (leftType is not ObjectExprType { Strict: true } strictObj)
        {
            return;
        }

        var propName = node.Token.AsSpan(expressionUtf8);
        if (!strictObj.TryGetProperty(propName, out _))
        {
            var propNameText = Encoding.UTF8.GetString(propName);
            var rootName = GetChainRootName(node.Left, nodes, expressionUtf8);
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"property '{propNameText}' is not defined in '{rootName}' object",
                expressionLocation));
        }
    }

    private static string GetChainRootName(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expressionUtf8)
    {
        var current = nodeId;
        while (current >= 0 && current < nodes.Length)
        {
            var n = nodes[current];
            if (n.Kind == ExpressionNodeKind.Identifier)
            {
                return Encoding.UTF8.GetString(n.Token.AsSpan(expressionUtf8));
            }

            current = n.Left;
        }

        return "object";
    }

    private static string ToContextText(ExpressionValidationContext context)
    {
        return context switch
        {
            ExpressionValidationContext.Workflow => "workflow",
            ExpressionValidationContext.WorkflowCallOutput => "workflow_call output",
            ExpressionValidationContext.Job => "job",
            ExpressionValidationContext.JobOutput => "job output",
            ExpressionValidationContext.ReusableWorkflowCallSecrets => "reusable workflow call secrets",
            ExpressionValidationContext.Step => "step",
            _ => "unknown",
        };
    }

    // ── Dynamic context property access validation ─────────────────────────────

    /// <summary>
    /// Validates property accesses in the expression using per-job context type overrides.
    /// Only produces property-access errors (no root context availability errors).
    /// Used by <c>ExprUndefinedVarRule</c> for dynamic contexts: steps, matrix, needs, inputs.
    /// </summary>
    public static Diagnostic[] ValidateDynamicPropertyAccess(
        ExpressionParseResult parseResult,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        (byte[] NameUtf8, ExprType Type)[] contextOverrides)
    {
        if (!parseResult.HasRoot || contextOverrides is null || contextOverrides.Length == 0)
        {
            return [];
        }

        var diagnostics = new List<Diagnostic>();
        ValidateNodePropertyAccess(
            parseResult.RootNode,
            parseResult.Nodes,
            parseResult.Arguments,
            expressionUtf8,
            expressionLocation,
            contextOverrides,
            diagnostics);
        return diagnostics.ToArray();
    }

    private static void ValidateNodePropertyAccess(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        (byte[] NameUtf8, ExprType Type)[] overrides,
        List<Diagnostic> diagnostics)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return;
        }

        var node = nodes[nodeId];
        switch (node.Kind)
        {
            case ExpressionNodeKind.Unary:
                ValidateNodePropertyAccess(node.Left, nodes, arguments, expressionUtf8, expressionLocation, overrides, diagnostics);
                break;
            case ExpressionNodeKind.Binary:
                ValidateNodePropertyAccess(node.Left, nodes, arguments, expressionUtf8, expressionLocation, overrides, diagnostics);
                ValidateNodePropertyAccess(node.Right, nodes, arguments, expressionUtf8, expressionLocation, overrides, diagnostics);
                break;
            case ExpressionNodeKind.MemberAccess:
                ValidateNodePropertyAccess(node.Left, nodes, arguments, expressionUtf8, expressionLocation, overrides, diagnostics);
                ValidatePropertyAccessWithOverrides(node, nodes, arguments, expressionUtf8, expressionLocation, overrides, diagnostics);
                break;
            case ExpressionNodeKind.WildcardAccess:
                ValidateNodePropertyAccess(node.Left, nodes, arguments, expressionUtf8, expressionLocation, overrides, diagnostics);
                break;
            case ExpressionNodeKind.IndexAccess:
                ValidateNodePropertyAccess(node.Left, nodes, arguments, expressionUtf8, expressionLocation, overrides, diagnostics);
                ValidateNodePropertyAccess(node.Right, nodes, arguments, expressionUtf8, expressionLocation, overrides, diagnostics);
                break;
            case ExpressionNodeKind.FunctionCall:
                ValidateNodePropertyAccess(node.Left, nodes, arguments, expressionUtf8, expressionLocation, overrides, diagnostics);
                for (var i = 0; i < node.ArgCount; i++)
                {
                    var argIndex = node.ArgStart + i;
                    if (argIndex >= 0 && argIndex < arguments.Length)
                    {
                        ValidateNodePropertyAccess(arguments[argIndex], nodes, arguments, expressionUtf8, expressionLocation, overrides, diagnostics);
                    }
                }

                break;
        }
    }

    private static void ValidatePropertyAccessWithOverrides(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        (byte[] NameUtf8, ExprType Type)[] overrides,
        List<Diagnostic> diagnostics)
    {
        var leftType = InferTypeWithOverrides(node.Left, nodes, arguments, expressionUtf8, overrides);
        if (leftType is not ObjectExprType { Strict: true } strictObj)
        {
            return;
        }

        var propName = node.Token.AsSpan(expressionUtf8);
        if (!strictObj.TryGetProperty(propName, out _))
        {
            var propNameText = Encoding.UTF8.GetString(propName);
            var rootName = GetChainRootName(node.Left, nodes, expressionUtf8);
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"property '{propNameText}' is not defined in '{rootName}' object",
                expressionLocation));
        }
    }

    private static ExprType InferTypeWithOverrides(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        (byte[] NameUtf8, ExprType Type)[] overrides)
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
            ExpressionNodeKind.Identifier => InferIdentifierTypeWithOverrides(node, expressionUtf8, overrides),
            ExpressionNodeKind.Unary => node.Operator == ExpressionOperator.Not
                ? ExprType.Bool
                : ExprType.Any,
            ExpressionNodeKind.Binary => InferBinaryType(node),
            ExpressionNodeKind.MemberAccess => InferMemberAccessTypeWithOverrides(node, nodes, arguments, expressionUtf8, overrides),
            ExpressionNodeKind.IndexAccess => InferIndexAccessTypeWithOverrides(node, nodes, arguments, expressionUtf8, overrides),
            ExpressionNodeKind.WildcardAccess => InferWildcardTypeWithOverrides(node, nodes, arguments, expressionUtf8, overrides),
            ExpressionNodeKind.FunctionCall => InferFunctionCallType(node, nodes, arguments, expressionUtf8),
            _ => ExprType.Any,
        };
    }

    private static ExprType InferIdentifierTypeWithOverrides(
        ExpressionNode node,
        ReadOnlySpan<byte> expressionUtf8,
        (byte[] NameUtf8, ExprType Type)[] overrides)
    {
        var name = node.Token.AsSpan(expressionUtf8);
        for (var i = 0; i < overrides.Length; i++)
        {
            if (EqualsAsciiIgnoreCase(name, overrides[i].NameUtf8))
            {
                return overrides[i].Type;
            }
        }

        if (TryGetBuiltinContextType(name, out var type))
        {
            return type;
        }

        return ExprType.Any;
    }

    private static ExprType InferMemberAccessTypeWithOverrides(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        (byte[] NameUtf8, ExprType Type)[] overrides)
    {
        var leftType = InferTypeWithOverrides(node.Left, nodes, arguments, expressionUtf8, overrides);
        if (leftType is ObjectExprType objectType)
        {
            if (objectType.TryGetProperty(node.Token.AsSpan(expressionUtf8), out var propertyType))
            {
                return propertyType;
            }

            return ExprType.Any;
        }

        if (leftType is ArrayExprType arrayType)
        {
            if (node.Token.AsSpan(expressionUtf8).SequenceEqual("*"u8))
            {
                return arrayType.ElementType;
            }
        }

        return ExprType.Any;
    }

    private static ExprType InferIndexAccessTypeWithOverrides(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        (byte[] NameUtf8, ExprType Type)[] overrides)
    {
        var leftType = InferTypeWithOverrides(node.Left, nodes, arguments, expressionUtf8, overrides);
        var rightType = InferTypeWithOverrides(node.Right, nodes, arguments, expressionUtf8, overrides);

        if (leftType is ArrayExprType arrayType)
        {
            if (rightType.IsAssignableTo(ExprType.Number) || rightType is AnyExprType)
            {
                return arrayType.ElementType;
            }

            return ExprType.Any;
        }

        if (leftType is ObjectExprType objectType)
        {
            if (node.Right >= 0
                && node.Right < nodes.Length
                && nodes[node.Right].Kind == ExpressionNodeKind.StringLiteral)
            {
                var propertyName = nodes[node.Right].Token.AsSpan(expressionUtf8);
                if (objectType.TryGetProperty(propertyName, out var propertyType))
                {
                    return propertyType;
                }
            }

            if (rightType.IsAssignableTo(ExprType.String) || rightType is AnyExprType)
            {
                return objectType.DynamicPropertyType ?? ExprType.Any;
            }
        }

        return ExprType.Any;
    }

    private static ExprType InferWildcardTypeWithOverrides(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8,
        (byte[] NameUtf8, ExprType Type)[] overrides)
    {
        var leftType = InferTypeWithOverrides(node.Left, nodes, arguments, expressionUtf8, overrides);
        if (leftType is ArrayExprType arrayType)
        {
            return arrayType.ElementType;
        }

        if (leftType is ObjectExprType objectType)
        {
            return objectType.DynamicPropertyType ?? ExprType.Any;
        }

        return ExprType.Any;
    }
}
