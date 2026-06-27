using System.Buffers.Text;
using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>Constant-folds GitHub Actions expressions for lint-time truthiness checks.</summary>
internal static class ExpressionConstantEvaluator
{
    internal enum ConstantKind : byte { NotConstant, Null, Bool, Number, String }

    internal readonly struct ConstantResult
    {
        public ConstantKind Kind { get; private init; }
        public bool BoolValue { get; private init; }
        public double NumberValue { get; private init; }
        public string? StringValue { get; private init; }

        public static readonly ConstantResult NotConst = new() { Kind = ConstantKind.NotConstant };
        public static readonly ConstantResult NullVal = new() { Kind = ConstantKind.Null };
        public static ConstantResult Bool(bool v) => new() { Kind = ConstantKind.Bool, BoolValue = v };
        public static ConstantResult Num(double v) => new() { Kind = ConstantKind.Number, NumberValue = v };
        public static ConstantResult Str(string v) => new() { Kind = ConstantKind.String, StringValue = v };

        public bool IsConstant => Kind != ConstantKind.NotConstant;

        public bool IsTruthy => Kind switch
        {
            ConstantKind.Null => false,
            ConstantKind.Bool => BoolValue,
            ConstantKind.Number => NumberValue != 0,
            ConstantKind.String => StringValue is { Length: > 0 },
            _ => false,
        };

        public string ToCoercedString() => Kind switch
        {
            ConstantKind.Null => "",
            ConstantKind.Bool => BoolValue ? "true" : "false",
            ConstantKind.Number => NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantKind.String => StringValue ?? "",
            _ => "",
        };
    }

    internal static bool TryEvaluateConstantBool(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        out bool value)
    {
        var result = TryEvaluateConstant(nodeId, nodes, arguments, expression);
        if (result.IsConstant)
        {
            value = result.IsTruthy;
            return true;
        }

        value = false;
        return false;
    }

    internal static ConstantResult TryEvaluateConstant(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return ConstantResult.NotConst;
        }

        var node = nodes[nodeId];

        switch (node.Kind)
        {
            case ExpressionNodeKind.BooleanLiteral:
                return ConstantResult.Bool(node.Token.AsSpan(expression).SequenceEqual("true"u8));

            case ExpressionNodeKind.NullLiteral:
                return ConstantResult.NullVal;

            case ExpressionNodeKind.NumberLiteral:
                {
                    var numToken = node.Token.AsSpan(expression);
                    if (Utf8Parser.TryParse(numToken, out double d, out _))
                    {
                        return ConstantResult.Num(d);
                    }

                    return ConstantResult.NotConst;
                }

            case ExpressionNodeKind.StringLiteral:
                {
                    var strToken = node.Token.AsSpan(expression);
                    return ConstantResult.Str(Encoding.UTF8.GetString(strToken));
                }

            case ExpressionNodeKind.Unary when node.Operator == ExpressionOperator.Not:
                {
                    var child = TryEvaluateConstant(node.Left, nodes, arguments, expression);
                    if (!child.IsConstant)
                    {
                        return ConstantResult.NotConst;
                    }

                    return ConstantResult.Bool(!child.IsTruthy);
                }

            case ExpressionNodeKind.Binary when node.Operator == ExpressionOperator.And:
                {
                    var left = TryEvaluateConstant(node.Left, nodes, arguments, expression);
                    if (!left.IsConstant)
                    {
                        return ConstantResult.NotConst;
                    }

                    if (!left.IsTruthy)
                    {
                        return left;
                    }

                    return TryEvaluateConstant(node.Right, nodes, arguments, expression);
                }

            case ExpressionNodeKind.Binary when node.Operator == ExpressionOperator.Or:
                {
                    var left = TryEvaluateConstant(node.Left, nodes, arguments, expression);
                    if (!left.IsConstant)
                    {
                        return ConstantResult.NotConst;
                    }

                    if (left.IsTruthy)
                    {
                        return left;
                    }

                    return TryEvaluateConstant(node.Right, nodes, arguments, expression);
                }

            case ExpressionNodeKind.FunctionCall:
                return TryEvaluateConstantFunction(node, nodes, arguments, expression);

            default:
                return ConstantResult.NotConst;
        }
    }

    private static ConstantResult TryEvaluateConstantFunction(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression)
    {
        if (node.Left < 0 || node.Left >= nodes.Length)
        {
            return ConstantResult.NotConst;
        }

        var funcNode = nodes[node.Left];
        if (funcNode.Kind != ExpressionNodeKind.Identifier)
        {
            return ConstantResult.NotConst;
        }

        var funcName = funcNode.Token.AsSpan(expression);
        var argResults = new ConstantResult[node.ArgCount];
        for (var i = 0; i < node.ArgCount; i++)
        {
            if (node.ArgStart + i >= arguments.Length)
            {
                return ConstantResult.NotConst;
            }

            argResults[i] = TryEvaluateConstant(arguments[node.ArgStart + i], nodes, arguments, expression);
            if (!argResults[i].IsConstant)
            {
                return ConstantResult.NotConst;
            }
        }

        if (funcName.SequenceEqual("contains"u8) && argResults.Length == 2)
        {
            var search = argResults[0].ToCoercedString();
            var item = argResults[1].ToCoercedString();
            return ConstantResult.Bool(search.Contains(item, StringComparison.OrdinalIgnoreCase));
        }

        if (funcName.SequenceEqual("startsWith"u8) && argResults.Length == 2)
        {
            var search = argResults[0].ToCoercedString();
            var prefix = argResults[1].ToCoercedString();
            return ConstantResult.Bool(search.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        if (funcName.SequenceEqual("endsWith"u8) && argResults.Length == 2)
        {
            var search = argResults[0].ToCoercedString();
            var suffix = argResults[1].ToCoercedString();
            return ConstantResult.Bool(search.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        if (funcName.SequenceEqual("format"u8) && argResults.Length >= 1)
        {
            var fmt = argResults[0].ToCoercedString();
            for (var i = 1; i < argResults.Length; i++)
            {
                fmt = fmt.Replace($"{{{i - 1}}}", argResults[i].ToCoercedString());
            }

            return ConstantResult.Str(fmt);
        }

        return ConstantResult.NotConst;
    }
}
