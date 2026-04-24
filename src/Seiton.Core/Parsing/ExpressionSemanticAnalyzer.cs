using System.Text;
using System.Text.Json;
using Seiton.Core.Generated;

using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;

namespace Seiton.Core.Parsing;

/// <summary>Identifies which part of the workflow an expression appears in, for context-sensitive validation.</summary>
public enum ExpressionValidationContext
{
    Workflow,
    WorkflowCallOutput,
    Job,
    JobOutput,
    ReusableWorkflowCallSecrets,
    Step,
    Strategy,
}

/// <summary>
/// Performs semantic analysis on parsed expression ASTs: context availability, function validation,
/// type inference, and property access checks.
/// </summary>
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

    /// <summary>Validates expression semantics (undefined contexts, unknown functions, type mismatches) and returns diagnostics.</summary>
    public static Diagnostic[] Validate(
        ExpressionParseResult parseResult,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        ExpressionValidationContext context,
        bool allowStatusCheckFunctions = false)
    {
        if (!parseResult.HasRoot)
        {
            return [];
        }

        var diagnostics = new List<Diagnostic>();
        ValidateNode(parseResult.RootNode, -1, parseResult.Nodes, parseResult.Arguments, expressionUtf8, expressionLocation, context, allowStatusCheckFunctions, diagnostics);
        return diagnostics.ToArray();
    }

    /// <summary>
    /// Internal fast path: validate expression using spans (no array allocation).
    /// Diagnostics are appended directly to the caller's list.
    /// </summary>
    internal static void ValidateInline(
        int rootNode,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        ExpressionValidationContext context,
        List<Diagnostic> diagnostics,
        bool allowStatusCheckFunctions = false)
    {
        if (rootNode < 0)
        {
            return;
        }

        ValidateNode(rootNode, -1, nodes, arguments, expressionUtf8, expressionLocation, context, allowStatusCheckFunctions, diagnostics);
    }

    /// <summary>Infers the static type of the expression node at <paramref name="nodeId"/>.</summary>
    public static ExprType InferType(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expressionUtf8)
    {
        return InferTypeSpan(nodeId, nodes, arguments, expressionUtf8);
    }

    /// <summary>
    /// Checks whether the inferred type of the expression is suitable for template interpolation (${{ }}).
    /// Object/array/null types do not produce meaningful string output when interpolated.
    /// Returns the diagnostic if a problem is detected, or null otherwise.
    /// </summary>
    public static Diagnostic? CheckTemplateType(
        ExpressionParseResult parseResult,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation)
    {
        if (!parseResult.HasRoot)
        {
            return null;
        }

        var type = InferTypeSpan(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expressionUtf8);
        return type switch
        {
            ObjectExprType => new Diagnostic(
                DiagnosticSeverity.Warning,
                "object value in ${{ }} will be converted to string \"[Object]\"",
                expressionLocation),
            ArrayExprType => new Diagnostic(
                DiagnosticSeverity.Warning,
                "array value in ${{ }} will be converted to string \"[Array]\"",
                expressionLocation),
            NullExprType => new Diagnostic(
                DiagnosticSeverity.Warning,
                "null value in ${{ }} will be converted to empty string",
                expressionLocation),
            _ => null,
        };
    }

    private static ExprType InferTypeSpan(
        int nodeId,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
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

    /// <summary>Looks up the min/max argument count for a built-in expression function by its UTF-8 name.</summary>
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
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        ExpressionValidationContext context,
        bool allowStatusCheckFunctions,
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
            ValidateFunctionCall(node, nodes, arguments, expressionUtf8, expressionLocation, allowStatusCheckFunctions, diagnostics);
        }

        if (node.Kind == ExpressionNodeKind.MemberAccess)
        {
            ValidateVarsNamingConvention(node, nodes, expressionUtf8, expressionLocation, diagnostics);
        }

        switch (node.Kind)
        {
            case ExpressionNodeKind.Unary:
                ValidateNode(node.Left, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, allowStatusCheckFunctions, diagnostics);
                ValidateUnaryOp(node, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
                break;
            case ExpressionNodeKind.Binary:
                ValidateNode(node.Left, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, allowStatusCheckFunctions, diagnostics);
                ValidateNode(node.Right, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, allowStatusCheckFunctions, diagnostics);
                ValidateCompareOp(node, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
                break;
            case ExpressionNodeKind.MemberAccess:
                ValidateNode(node.Left, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, allowStatusCheckFunctions, diagnostics);
                ValidatePropertyAccess(node, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
                break;
            case ExpressionNodeKind.WildcardAccess:
                ValidateNode(node.Left, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, allowStatusCheckFunctions, diagnostics);
                ValidateWildcardAccess(node, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
                break;
            case ExpressionNodeKind.IndexAccess:
                ValidateNode(node.Left, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, allowStatusCheckFunctions, diagnostics);
                ValidateNode(node.Right, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, allowStatusCheckFunctions, diagnostics);
                ValidateIndexAccess(node, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
                break;
            case ExpressionNodeKind.FunctionCall:
                ValidateNode(node.Left, nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, allowStatusCheckFunctions, diagnostics);
                for (var i = 0; i < node.ArgCount; i++)
                {
                    var argIndex = node.ArgStart + i;
                    if (argIndex >= 0 && argIndex < arguments.Length)
                    {
                        ValidateNode(arguments[argIndex], nodeId, nodes, arguments, expressionUtf8, expressionLocation, context, allowStatusCheckFunctions, diagnostics);
                    }
                }
                break;
        }
    }

    private static void ValidateFunctionCall(
        ExpressionNode node,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        bool allowStatusCheckFunctions,
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

        if (!allowStatusCheckFunctions && IsStatusCheckFunction(nameUtf8))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"status check function '{Encoding.UTF8.GetString(nameUtf8)}()' is only available in 'if' conditions",
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
                argTypes[i] = InferTypeSpan(arguments[argIndex], nodes, arguments, expressionUtf8);
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
            ValidateFromJsonLiteral(node, nameUtf8, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
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
        ValidateFromJsonLiteral(node, nameUtf8, nodes, arguments, expressionUtf8, expressionLocation, diagnostics);
    }

    private static void ValidateFormatPlaceholders(
        ExpressionNode functionCallNode,
        ReadOnlySpan<byte> functionNameUtf8,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
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

        // Track which placeholder indices are actually used
        var usedPlaceholders = 0uL; // Bit set for indices 0-63 (more than enough for practical use)

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

            if (indexValue < 64)
            {
                usedPlaceholders |= 1uL << indexValue;
            }

            i = j;
        }

        // Check for excess arguments (supplied but no placeholder references them)
        for (var argIdx = 0; argIdx < formatArgCount; argIdx++)
        {
            if (argIdx < 64 && (usedPlaceholders & (1uL << argIdx)) == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    $"format string does not contain placeholder {{{argIdx}}}; remove argument which is unused",
                    expressionLocation));
                return;
            }
        }
    }

    private static void ValidateFromJsonLiteral(
        ExpressionNode functionCallNode,
        ReadOnlySpan<byte> functionNameUtf8,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        if (!EqualsAsciiIgnoreCase(functionNameUtf8, "fromJSON"u8))
        {
            return;
        }

        if (functionCallNode.ArgCount == 0 || functionCallNode.ArgStart < 0 || functionCallNode.ArgStart >= arguments.Length)
        {
            return;
        }

        var argNodeId = arguments[functionCallNode.ArgStart];
        if (argNodeId < 0 || argNodeId >= nodes.Length)
        {
            return;
        }

        var argNode = nodes[argNodeId];
        if (argNode.Kind != ExpressionNodeKind.StringLiteral)
        {
            return;
        }

        var jsonText = Encoding.UTF8.GetString(argNode.Token.AsSpan(expressionUtf8));
        try
        {
            using var document = JsonDocument.Parse(jsonText);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"fromJSON() argument is not valid JSON: {ex.Message}",
                expressionLocation));
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
        // Comparison/equality operators always produce bool.
        // Logical operators (&&, ||) in GitHub Actions return the operand value, not a coerced
        // boolean (short-circuit semantics like JavaScript). Infer as Any because the result
        // type depends on the runtime values of both branches.
        return node.Operator switch
        {
            ExpressionOperator.Equal
                or ExpressionOperator.NotEqual
                or ExpressionOperator.Less
                or ExpressionOperator.LessOrEqual
                or ExpressionOperator.Greater
                or ExpressionOperator.GreaterOrEqual => ExprType.Bool,
            ExpressionOperator.And
                or ExpressionOperator.Or => ExprType.Any,
            _ => ExprType.Any,
        };
    }

    private static ExprType InferMemberAccessType(
        ExpressionNode node,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8)
    {
        var leftType = InferTypeSpan(node.Left, nodes, arguments, expressionUtf8);
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
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8)
    {
        var leftType = InferTypeSpan(node.Left, nodes, arguments, expressionUtf8);
        var rightType = InferTypeSpan(node.Right, nodes, arguments, expressionUtf8);

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
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8)
    {
        var leftType = InferTypeSpan(node.Left, nodes, arguments, expressionUtf8);
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
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
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

    private static ExprType? TryInferFromJsonLiteral(int nodeId, ReadOnlySpan<ExpressionNode> nodes, ReadOnlySpan<byte> expressionUtf8)
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

                    return ExprType.Object(properties, strict: true);
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
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        var leftType = InferTypeSpan(node.Left, nodes, arguments, expressionUtf8);
        var rightType = InferTypeSpan(node.Right, nodes, arguments, expressionUtf8);

        // Ordering operators (<, <=, >, >=): reject null, bool, object, array
        if (IsComparisonOperator(node.Operator))
        {
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

            return;
        }

        // Equality operators (==, !=): warn about cross-type comparisons
        if (node.Operator is ExpressionOperator.Equal or ExpressionOperator.NotEqual)
        {
            if (leftType is AnyExprType || rightType is AnyExprType)
            {
                return;
            }

            if (!AreEqualityCompatible(leftType, rightType))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"{leftType.TypeName} value cannot be compared to {rightType.TypeName} value with '{OperatorSymbol(node.Operator)}' operator",
                    expressionLocation));
            }
        }
    }

    /// <summary>
    /// Returns true when two concrete types can be meaningfully compared with == or !=.
    /// GitHub Actions coerces types for equality, but object vs string, bool vs number etc. are suspicious.
    /// </summary>
    private static bool AreEqualityCompatible(ExprType left, ExprType right)
    {
        // Same base type is always compatible
        if (left.GetType() == right.GetType())
        {
            return true;
        }

        // null can be compared with anything
        if (left is NullExprType || right is NullExprType)
        {
            return true;
        }

        // string and number: GitHub Actions coerces for comparison — allow
        if ((left is StringExprType && right is NumberExprType)
            || (left is NumberExprType && right is StringExprType))
        {
            return true;
        }

        // string and bool: GitHub Actions coerces 'true'/'false' strings — allow
        if ((left is StringExprType && right is BoolExprType)
            || (left is BoolExprType && right is StringExprType))
        {
            return true;
        }

        return false;
    }

    private static void ValidateUnaryOp(
        ExpressionNode node,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        if (node.Operator != ExpressionOperator.Not)
        {
            return;
        }

        var operandType = InferTypeSpan(node.Left, nodes, arguments, expressionUtf8);
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
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        var leftType = InferTypeSpan(node.Left, nodes, arguments, expressionUtf8);
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
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        var leftType = InferTypeSpan(node.Left, nodes, arguments, expressionUtf8);
        var rightType = InferTypeSpan(node.Right, nodes, arguments, expressionUtf8);

        if (leftType is ArrayExprType && rightType is not (AnyExprType or NumberExprType))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"index of array must be number, but got {rightType.TypeName}",
                expressionLocation));
        }
        else if (leftType is ObjectExprType objType && rightType is not (AnyExprType or StringExprType))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"index of object must be string, but got {rightType.TypeName}",
                expressionLocation));
        }
        else if (leftType is ObjectExprType { Strict: true } strictObj
            && node.Right >= 0
            && node.Right < nodes.Length
            && nodes[node.Right].Kind == ExpressionNodeKind.StringLiteral)
        {
            var propertyName = nodes[node.Right].Token.AsSpan(expressionUtf8);
            if (!strictObj.TryGetProperty(propertyName, out _))
            {
                var propNameText = Encoding.UTF8.GetString(propertyName);
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"property \"{propNameText}\" is not defined in object type {FormatObjectType(strictObj)}",
                    expressionLocation));
            }
        }
    }

    private static string OperatorSymbol(ExpressionOperator op)
    {
        return op switch
        {
            ExpressionOperator.Equal => "==",
            ExpressionOperator.NotEqual => "!=",
            ExpressionOperator.Less => "<",
            ExpressionOperator.LessOrEqual => "<=",
            ExpressionOperator.Greater => ">",
            ExpressionOperator.GreaterOrEqual => ">=",
            _ => op.ToString(),
        };
    }

    private static bool IsStatusCheckFunction(ReadOnlySpan<byte> nameUtf8)
    {
        return EqualsAsciiIgnoreCase(nameUtf8, "success"u8)
            || EqualsAsciiIgnoreCase(nameUtf8, "failure"u8)
            || EqualsAsciiIgnoreCase(nameUtf8, "cancelled"u8)
            || EqualsAsciiIgnoreCase(nameUtf8, "always"u8);
    }

    private static void ValidateVarsNamingConvention(
        ExpressionNode node,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        if (node.Left < 0 || node.Left >= nodes.Length)
        {
            return;
        }

        var left = nodes[node.Left];
        if (left.Kind != ExpressionNodeKind.Identifier)
        {
            return;
        }

        var leftName = left.Token.AsSpan(expressionUtf8);
        if (!EqualsAsciiIgnoreCase(leftName, "vars"u8))
        {
            return;
        }

        var propName = node.Token.AsSpan(expressionUtf8);
        if (propName.Length == 0)
        {
            return;
        }

        // Check GITHUB_ prefix prohibition (case-insensitive)
        if (propName.Length >= 7 && EqualsAsciiIgnoreCase(propName[..7], "GITHUB_"u8))
        {
            var propNameText = Encoding.UTF8.GetString(propName);
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"configuration variable name '{propNameText}' must not start with 'GITHUB_' prefix",
                expressionLocation));
            return;
        }

        // Check valid characters: must start with [A-Za-z_], rest must be [A-Za-z0-9_]
        if (!IsValidVarsName(propName))
        {
            var propNameText = Encoding.UTF8.GetString(propName);
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"configuration variable name '{propNameText}' contains invalid characters (must match [a-zA-Z_][a-zA-Z0-9_]*)",
                expressionLocation));
        }
    }

    private static bool IsValidVarsName(ReadOnlySpan<byte> name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        var first = name[0];
        if (!IsAsciiLetter(first) && first != (byte)'_')
        {
            return false;
        }

        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!IsAsciiLetter(c) && !IsAsciiDigit(c) && c != (byte)'_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(byte c) => (c >= (byte)'A' && c <= (byte)'Z') || (c >= (byte)'a' && c <= (byte)'z');

    private static bool IsAsciiDigit(byte c) => c >= (byte)'0' && c <= (byte)'9';

    private static void ValidatePropertyAccess(
        ExpressionNode node,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        List<Diagnostic> diagnostics)
    {
        var leftType = InferTypeSpan(node.Left, nodes, arguments, expressionUtf8);

        // String dereference: accessing .property on a string value is an error
        if (leftType is StringExprType)
        {
            var propName = Encoding.UTF8.GetString(node.Token.AsSpan(expressionUtf8));
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"receiver of object dereference \"{propName}\" must be type of object but got \"string\"",
                expressionLocation));
            return;
        }

        if (leftType is not ObjectExprType { Strict: true } strictObj)
        {
            return;
        }

        var propNameSpan = node.Token.AsSpan(expressionUtf8);
        if (!strictObj.TryGetProperty(propNameSpan, out _))
        {
            var propNameText = Encoding.UTF8.GetString(propNameSpan);
            var rootName = GetChainRootName(node.Left, nodes, expressionUtf8);
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"property '{propNameText}' is not defined in '{rootName}' object",
                expressionLocation));
        }
    }

    private static string GetChainRootName(int nodeId, ReadOnlySpan<ExpressionNode> nodes, ReadOnlySpan<byte> expressionUtf8)
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

    private static string FormatObjectType(ObjectExprType objectType)
    {
        if (objectType.Properties is null || objectType.Properties.Count == 0)
        {
            return "{}";
        }

        var sb = new System.Text.StringBuilder("{");
        var first = true;
        foreach (var pair in objectType.Properties)
        {
            if (!first)
            {
                sb.Append("; ");
            }

            sb.Append(Encoding.UTF8.GetString(pair.Key.Span));
            sb.Append(": ");
            sb.Append(pair.Value.TypeName);
            first = false;
        }

        sb.Append('}');
        return sb.ToString();
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
            ExpressionValidationContext.Strategy => "strategy",
            _ => "unknown",
        };
    }

    // Dynamic context property access validation

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

    /// <summary>
    /// Inline variant of <see cref="ValidateDynamicPropertyAccess"/>. Appends diagnostics to caller-supplied list,
    /// avoiding per-call <c>new List&lt;Diagnostic&gt;()</c> and <c>ToArray()</c> allocations.
    /// </summary>
    internal static void ValidateDynamicPropertyAccessInline(
        ExpressionParseResult parseResult,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        (byte[] NameUtf8, ExprType Type)[] contextOverrides,
        List<Diagnostic> diagnostics)
    {
        if (!parseResult.HasRoot || contextOverrides is null || contextOverrides.Length == 0)
        {
            return;
        }

        ValidateNodePropertyAccess(
            parseResult.RootNode,
            parseResult.Nodes,
            parseResult.Arguments,
            expressionUtf8,
            expressionLocation,
            contextOverrides,
            diagnostics);
    }

    private static void ValidateNodePropertyAccess(
        int nodeId,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
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
                ValidateCompareOpWithOverrides(node, nodes, arguments, expressionUtf8, expressionLocation, overrides, diagnostics);
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
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
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

    /// <summary>
    /// Validates comparison and equality operators using context-override–aware type inference.
    /// This catches type mismatches (e.g. <c>bool &gt; number</c>) that the parser-level
    /// <see cref="ValidateCompareOp"/> cannot detect because dynamic context types (inputs, matrix, etc.)
    /// resolve to <see cref="AnyExprType"/> without overrides.
    /// </summary>
    private static void ValidateCompareOpWithOverrides(
        ExpressionNode node,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        (byte[] NameUtf8, ExprType Type)[] overrides,
        List<Diagnostic> diagnostics)
    {
        var leftType = InferTypeWithOverrides(node.Left, nodes, arguments, expressionUtf8, overrides);
        var rightType = InferTypeWithOverrides(node.Right, nodes, arguments, expressionUtf8, overrides);

        // Ordering operators (<, <=, >, >=): reject null, bool, object, array
        if (IsComparisonOperator(node.Operator))
        {
            // Skip if both sides are still Any (overrides didn't help resolve types)
            if (leftType is AnyExprType && rightType is AnyExprType)
            {
                return;
            }

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

            return;
        }

        // Equality operators (==, !=): warn about cross-type comparisons
        if (node.Operator is ExpressionOperator.Equal or ExpressionOperator.NotEqual)
        {
            if (leftType is AnyExprType || rightType is AnyExprType)
            {
                return;
            }

            if (!AreEqualityCompatible(leftType, rightType))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"{leftType.TypeName} value cannot be compared to {rightType.TypeName} value with '{OperatorSymbol(node.Operator)}' operator",
                    expressionLocation));
            }
        }
    }

    private static ExprType InferTypeWithOverrides(
        int nodeId,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
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
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
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
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
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
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
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
