using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class IfCondRule : RuleBase
{
    public override string Id => "if-cond";

    public override string Name => "If Condition Rule";

    public override void VisitJobPre(Job job)
    {
        ValidateCondition(job.If, job, null);
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

        var expression = TryExtractExpressionBody(raw, out var body) ? body : raw;

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

        if (IsConstantBool(parseResult.RootNode, parseResult.Nodes, expression, out var value))
        {
            var boolText = value ? "true" : "false";
            if (job is not null)
            {
                AddJobWarning(job, $"job if condition is always {boolText}", Arena.GetStringRange(condition));
            }

            if (step is not null)
            {
                AddStepWarning(step, $"step if condition is always {boolText}", Arena.GetStringRange(condition));
            }
        }
    }

    private static bool IsConstantBool(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> expression, out bool value)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            value = false;
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind == ExpressionNodeKind.BooleanLiteral)
        {
            var token = node.Token.AsSpan(expression);
            if (token.SequenceEqual("true"u8))
            {
                value = true;
                return true;
            }

            if (token.SequenceEqual("false"u8))
            {
                value = false;
                return true;
            }
        }

        if (node.Kind == ExpressionNodeKind.Unary && node.Operator == ExpressionOperator.Not)
        {
            if (IsConstantBool(node.Left, nodes, expression, out var child))
            {
                value = !child;
                return true;
            }
        }

        if (node.Kind == ExpressionNodeKind.Binary)
        {
            if (node.Operator == ExpressionOperator.And
                && IsConstantBool(node.Left, nodes, expression, out var leftAnd)
                && IsConstantBool(node.Right, nodes, expression, out var rightAnd))
            {
                value = leftAnd && rightAnd;
                return true;
            }

            if (node.Operator == ExpressionOperator.Or
                && IsConstantBool(node.Left, nodes, expression, out var leftOr)
                && IsConstantBool(node.Right, nodes, expression, out var rightOr))
            {
                value = leftOr || rightOr;
                return true;
            }
        }

        value = false;
        return false;
    }

    private static bool TryExtractExpressionBody(ReadOnlySpan<byte> value, out ReadOnlySpan<byte> body)
    {
        body = value;

        var open = value.IndexOf("${{"u8);
        if (open < 0)
        {
            return false;
        }

        var close = value.LastIndexOf("}}"u8);
        if (close < 0)
        {
            return false;
        }

        if (open + 3 > close)
        {
            return false;
        }

        if (open != 0)
        {
            return false;
        }

        var tail = close + 2;
        for (var i = tail; i < value.Length; i++)
        {
            var b = value[i];
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        body = TrimAsciiWhiteSpace(value.Slice(open + 3, close - (open + 3)));
        return true;
    }
}
