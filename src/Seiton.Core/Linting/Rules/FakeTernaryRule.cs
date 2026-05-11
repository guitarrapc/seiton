using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags the <c>&amp;&amp; ... || ...</c> fake ternary pattern which has surprising short-circuit behavior.</summary>
public sealed class FakeTernaryRule() : RuleBase(RuleId.FakeTernary)
{
    public override string Name => "Fake Ternary Rule";

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

        var expression = ExpressionScanHelpers.TryExtractExpressionBody(raw, out var body) ? body : raw;

        var parseResult = Config.ParseExpression(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return;
        }

        if (!ContainsFakeTernary(parseResult.RootNode, parseResult.Nodes.Span, parseResult.Arguments.Span, expression))
        {
            return;
        }

        const string message = "avoid fake ternary pattern 'cond && a || b'; use a case expression (or equivalent explicit branching)";
        if (job is not null)
        {
            AddJobWarning(job, message, Arena.GetStringRange(condition));
        }

        if (step is not null)
        {
            AddStepWarning(step, message, Arena.GetStringRange(condition));
        }
    }

    private static bool ContainsFakeTernary(int nodeId, ReadOnlySpan<ExpressionNode> nodes, ReadOnlySpan<int> arguments, ReadOnlySpan<byte> expression)
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

    private static bool ContainsFakeTernaryInArguments(
        ExpressionNode node,
        ReadOnlySpan<ExpressionNode> nodes,
        ReadOnlySpan<int> arguments,
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

    private static bool IsBooleanType(ExprType type)
    {
        return type is BoolExprType;
    }
}
