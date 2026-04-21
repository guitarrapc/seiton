using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class FakeTernaryRule : RuleBase
{
    public override string Id => "fake-ternary";

    public override string Name => "Fake Ternary Rule";

    public override void VisitJobPre(Job job)
    {
        ValidateCondition(job.If, job, null);
    }

    public override void VisitStep(Step step)
    {
        ValidateCondition(step.If, null, step);
    }

    void ValidateCondition(StringNode? condition, Job? job, Step? step)
    {
        if (condition is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var raw = condition.Value.AsSpan(Config.Utf8Yaml);
        if (raw.Length == 0)
        {
            return;
        }

        var expression = TryExtractExpressionBody(raw, out var body) ? body : raw;

        var parseResult = Config.ParseExpression(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return;
        }

        if (!ContainsFakeTernary(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression))
        {
            return;
        }

        const string message = "avoid fake ternary pattern 'cond && a || b'; use a case expression (or equivalent explicit branching)";
        if (job is not null)
        {
            AddJobWarning(job, message, condition.Range);
        }

        if (step is not null)
        {
            AddStepWarning(step, message, condition.Range);
        }
    }

    static bool ContainsFakeTernary(int nodeId, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expression)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind == ExpressionNodeKind.Binary
            && node.Operator == ExpressionOperator.Or
            && node.Left >= 0
            && node.Left < nodes.Length)
        {
            var left = nodes[node.Left];
            if (left.Kind == ExpressionNodeKind.Binary
                && left.Operator == ExpressionOperator.And
                && left.Right >= 0
                && left.Right < nodes.Length
                && node.Right >= 0
                && node.Right < nodes.Length)
            {
                var trueArmType = ExpressionSemanticAnalyzer.InferType(left.Right, nodes, arguments, expression);
                var falseArmType = ExpressionSemanticAnalyzer.InferType(node.Right, nodes, arguments, expression);
                if (!IsBooleanType(trueArmType) || !IsBooleanType(falseArmType))
                {
                    return true;
                }
            }
        }

        return node.Kind switch
        {
            ExpressionNodeKind.Unary => ContainsFakeTernary(node.Left, nodes, arguments, expression),
            ExpressionNodeKind.Binary => ContainsFakeTernary(node.Left, nodes, arguments, expression)
                || ContainsFakeTernary(node.Right, nodes, arguments, expression),
            ExpressionNodeKind.MemberAccess or ExpressionNodeKind.WildcardAccess =>
                ContainsFakeTernary(node.Left, nodes, arguments, expression),
            ExpressionNodeKind.IndexAccess => ContainsFakeTernary(node.Left, nodes, arguments, expression)
                || ContainsFakeTernary(node.Right, nodes, arguments, expression),
            ExpressionNodeKind.FunctionCall => ContainsFakeTernary(node.Left, nodes, arguments, expression)
                || ContainsFakeTernaryInArguments(node, nodes, arguments, expression),
            _ => false,
        };
    }

    static bool ContainsFakeTernaryInArguments(
        ExpressionNode node,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression)
    {
        for (var i = 0; i < node.ArgCount; i++)
        {
            var argIndex = node.ArgStart + i;
            if (argIndex < 0 || argIndex >= arguments.Length)
            {
                continue;
            }

            if (ContainsFakeTernary(arguments[argIndex], nodes, arguments, expression))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsBooleanType(ExprType type)
    {
        return type is BoolExprType;
    }

    static bool TryExtractExpressionBody(ReadOnlySpan<byte> value, out ReadOnlySpan<byte> body)
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
