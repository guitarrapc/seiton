using System.Buffers.Text;
using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Checks <c>if:</c> conditions for common mistakes (e.g. missing expression delimiters, always-true patterns).</summary>
public sealed class IfCondRule() : RuleBase(RuleId.IfCond)
{
    public override string Name => "If Condition Rule";

    public override void VisitJobPre(Job job)
    {
        ValidateCondition(job.If, job, null);

        // snapshot.if
        if (job.Snapshot is { } snapshot)
        {
            ValidateCondition(snapshot.If, job, null);
        }
    }

    public override void VisitStep(Step step)
    {
        ValidateCondition(step.If, null, step);
    }

    private void ValidateCondition(StringNodeId condition, Job? job, Step? step)
    {
        if (!condition.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var raw = Arena.GetStringValue(condition);
        if (raw.Length == 0)
        {
            return;
        }

        // Detect "always true" pattern: value contains ${{ }} but has extra characters around it.
        // GitHub Actions evaluates the entire string as a template, producing a non-empty string → always truthy.
        // Examples: "${{ expr }}\n" (block scalar), "${{ expr }} " (trailing space), "${{ e1 }} && ${{ e2 }}"
        if (IsAlwaysTrueTemplate(raw))
        {
            var conditionText = FormatConditionText(raw);
            var message = $"if: condition \"{conditionText}\" is always evaluated to true because extra characters are around ${{{{ }}}}";
            if (job is not null)
            {
                AddJobWarning(job, message, Arena.GetStringRange(condition));
            }

            if (step is not null)
            {
                AddStepWarning(step, message, Arena.GetStringRange(condition));
            }

            return;
        }

        var expression = ExpressionScanHelpers.TryExtractExpressionBody(raw, out var body) ? body : raw;

        var parseResult = Config.ParseExpression(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            if (job is not null)
            {
                AddJobWarning(job, "job if condition contains syntax errors", Arena.GetStringRange(condition));
            }

            if (step is not null)
            {
                AddStepWarning(step, "step if condition contains syntax errors", Arena.GetStringRange(condition));
            }

            return;
        }

        if (IsConstantBool(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression, out var value))
        {
            var expressionText = Encoding.UTF8.GetString(expression).Trim();
            var message = $"constant expression \"{expressionText}\" in condition. remove the if: section";
            if (job is not null)
            {
                AddJobWarning(job, message, Arena.GetStringRange(condition));
            }

            if (step is not null)
            {
                AddStepWarning(step, message, Arena.GetStringRange(condition));
            }
        }
    }

    private static bool IsConstantBool(int nodeId, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression, out bool value)
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

    /// <summary>Result of constant expression evaluation.</summary>
    private enum ConstantKind : byte { NotConstant, Null, Bool, Number, String }

    private readonly struct ConstantResult
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

    private static ConstantResult TryEvaluateConstant(int nodeId, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression)
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
                    return left; // short-circuit: falsy && x → falsy
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
                    return left; // short-circuit: truthy || x → truthy
                }

                return TryEvaluateConstant(node.Right, nodes, arguments, expression);
            }

            case ExpressionNodeKind.FunctionCall:
                return TryEvaluateConstantFunction(node, nodes, arguments, expression);

            default:
                return ConstantResult.NotConst;
        }
    }

    /// <summary>Evaluates a function call with all-constant arguments for known pure functions.</summary>
    private static ConstantResult TryEvaluateConstantFunction(ExpressionNode node, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression)
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

        // Evaluate all arguments — bail if any is not constant
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

        // contains(search, item) → bool (case-insensitive)
        if (funcName.SequenceEqual("contains"u8) && argResults.Length == 2)
        {
            var search = argResults[0].ToCoercedString();
            var item = argResults[1].ToCoercedString();
            return ConstantResult.Bool(search.Contains(item, StringComparison.OrdinalIgnoreCase));
        }

        // startsWith(search, prefix) → bool (case-insensitive)
        if (funcName.SequenceEqual("startsWith"u8) && argResults.Length == 2)
        {
            var search = argResults[0].ToCoercedString();
            var prefix = argResults[1].ToCoercedString();
            return ConstantResult.Bool(search.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        // endsWith(search, suffix) → bool (case-insensitive)
        if (funcName.SequenceEqual("endsWith"u8) && argResults.Length == 2)
        {
            var search = argResults[0].ToCoercedString();
            var suffix = argResults[1].ToCoercedString();
            return ConstantResult.Bool(search.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        // format(fmt, args...) → string
        if (funcName.SequenceEqual("format"u8) && argResults.Length >= 1)
        {
            var fmt = argResults[0].ToCoercedString();
            for (var i = 1; i < argResults.Length; i++)
            {
                fmt = fmt.Replace($"{{{i - 1}}}", argResults[i].ToCoercedString());
            }

            return ConstantResult.Str(fmt);
        }

        return ConstantResult.NotConst; // Unknown or impure function
    }

    /// <summary>
    /// Detects "always evaluated to true" patterns where <c>${{ }}</c> is present but extra characters
    /// are around it (leading text, trailing newline/space, or multiple expression blocks).
    /// GitHub Actions treats such values as string templates that produce non-empty strings → always truthy.
    /// </summary>
    private static bool IsAlwaysTrueTemplate(ReadOnlySpan<byte> value)
    {
        var firstOpen = value.IndexOf("${{"u8);
        if (firstOpen < 0)
        {
            return false; // No expression delimiter at all
        }

        // Leading text before first ${{ → always true
        if (firstOpen > 0)
        {
            return true;
        }

        // firstOpen == 0: starts with ${{
        // Find the first matching }}
        var firstClose = value.Slice(3).IndexOf("}}"u8);
        if (firstClose < 0)
        {
            return false; // Malformed, let syntax error path handle it
        }

        firstClose += 3; // Adjust to absolute position

        var tail = firstClose + 2;

        // Check for another ${{ after the first }} → multiple expression blocks → always true
        if (tail < value.Length && ExpressionScanHelpers.ContainsExpressionMarker(value.Slice(tail)))
        {
            return true;
        }

        // Check trailing characters after }}: any characters at all mean "extra characters around ${{ }}".
        // A clean expression wrapper has nothing after }}.
        if (tail < value.Length)
        {
            return true;
        }

        return false;
    }

    /// <summary>Converts raw UTF-8 condition bytes to a displayable string, escaping newlines.</summary>
    private static string FormatConditionText(ReadOnlySpan<byte> raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        // Trim trailing newline that block scalars produce, but show it as \n in the message
        if (text.EndsWith('\n'))
        {
            text = text.TrimEnd('\n') + "\\n";
        }

        return text;
    }
}
