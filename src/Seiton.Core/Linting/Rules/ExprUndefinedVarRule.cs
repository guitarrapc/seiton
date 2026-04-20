using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

using static Seiton.Core.Parsing.ExpressionScanHelpers;

namespace Seiton.Core.Linting.Rules;

public sealed class ExprUndefinedVarRule : RuleBase
{
    public override string Id => "expr-undefined-var";

    public override string Name => "Expr Undefined Var Rule";

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        CheckNode(job.If, ExpressionValidationContext.Job, "job.if", static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        CheckEnv(job.Env, ExpressionValidationContext.Job, "job.env", static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        var callInputs = job.WorkflowCall?.Inputs;
        if (callInputs is null || callInputs.Count == 0)
        {
            return;
        }

        foreach (var pair in callInputs)
        {
            var input = pair.Value;
            var inputName = Decode(input.Name.Value);
            CheckNode(input.Value, ExpressionValidationContext.Job, $"job.with.{inputName}", static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }
    }

    public override void VisitStep(Step step)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        CheckNode(step.If, ExpressionValidationContext.Step, "step.if", static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        CheckEnv(step.Env, ExpressionValidationContext.Step, "step.env", static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        if (step.Exec is not ExecAction action || action.Inputs is null || action.Inputs.Count == 0)
        {
            return;
        }

        foreach (var pair in action.Inputs)
        {
            var inputName = Decode(pair.Key);
            CheckNode(pair.Value, ExpressionValidationContext.Step, $"step.with.{inputName}", static (rule, message, location, targetStep) =>
                rule.AddStepError(targetStep, message, location), step);
        }
    }

    void CheckEnv<TTarget>(
        Env? env,
        ExpressionValidationContext context,
        string sinkName,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (env is null)
        {
            return;
        }

        CheckNode(env.Expression, context, sinkName, report, target);

        var vars = env.Vars;
        if (vars is null || vars.Count == 0)
        {
            return;
        }

        foreach (var pair in vars)
        {
            var envVar = pair.Value;
            var keyName = Decode(envVar.Name.Value);
            CheckNode(envVar.Value, context, $"{sinkName}.{keyName}", report, target);
        }
    }

    void CheckNode<TTarget>(
        StringNode? node,
        ExpressionValidationContext context,
        string sinkName,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (node is null || Config.Utf8Yaml is null)
        {
            return;
        }

        var value = node.Value.AsSpan(Config.Utf8Yaml);
        if (value.Length == 0)
        {
            return;
        }

        var hasEmbeddedExpression = value.IndexOf("${{"u8) >= 0;
        var parseWholeValue = sinkName.EndsWith(".if", StringComparison.Ordinal);

        if (parseWholeValue && !hasEmbeddedExpression)
        {
            ValidateExpression(value, context, sinkName, node.Range, report, target);
            return;
        }

        var searchStart = 0;
        while (TryFindEmbeddedExpression(value, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;
            var expression = TrimAsciiWhiteSpace(value.Slice(bodyStart, bodyLength));
            if (expression.Length == 0)
            {
                continue;
            }

            ValidateExpression(expression, context, sinkName, node.Range, report, target);
        }
    }

    void ValidateExpression<TTarget>(
        ReadOnlySpan<byte> expression,
        ExpressionValidationContext context,
        string sinkName,
        TextRange location,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        var parseResult = ExpressionParser.Parse(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return;
        }

        VisitExpressionNode(
            parseResult.RootNode,
            parentId: -1,
            parseResult.Nodes,
            parseResult.Arguments,
            expression,
            context,
            sinkName,
            location,
            report,
            target);
    }

    void VisitExpressionNode<TTarget>(
        int nodeId,
        int parentId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        ExpressionValidationContext context,
        string sinkName,
        TextRange location,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return;
        }

        var node = nodes[nodeId];
        if (node.Kind == ExpressionNodeKind.Identifier && IsContextRootIdentifier(nodeId, parentId, nodes))
        {
            var rootName = node.Token.AsSpan(expression);
            if (!Availability.IsRootContextAvailable(context, rootName))
            {
                var rootNameText = Encoding.UTF8.GetString(rootName);
                report(
                    this,
                    $"{sinkName} expression references undefined context '{rootNameText}' in {ToContextText(context)} scope",
                    location,
                    target);
            }
        }

        switch (node.Kind)
        {
            case ExpressionNodeKind.Unary:
                VisitExpressionNode(node.Left, nodeId, nodes, arguments, expression, context, sinkName, location, report, target);
                break;
            case ExpressionNodeKind.Binary:
                VisitExpressionNode(node.Left, nodeId, nodes, arguments, expression, context, sinkName, location, report, target);
                VisitExpressionNode(node.Right, nodeId, nodes, arguments, expression, context, sinkName, location, report, target);
                break;
            case ExpressionNodeKind.MemberAccess:
            case ExpressionNodeKind.WildcardAccess:
                VisitExpressionNode(node.Left, nodeId, nodes, arguments, expression, context, sinkName, location, report, target);
                break;
            case ExpressionNodeKind.IndexAccess:
                VisitExpressionNode(node.Left, nodeId, nodes, arguments, expression, context, sinkName, location, report, target);
                VisitExpressionNode(node.Right, nodeId, nodes, arguments, expression, context, sinkName, location, report, target);
                break;
            case ExpressionNodeKind.FunctionCall:
                VisitExpressionNode(node.Left, nodeId, nodes, arguments, expression, context, sinkName, location, report, target);
                for (var i = 0; i < node.ArgCount; i++)
                {
                    var argIndex = node.ArgStart + i;
                    if (argIndex < 0 || argIndex >= arguments.Length)
                    {
                        continue;
                    }

                    VisitExpressionNode(arguments[argIndex], nodeId, nodes, arguments, expression, context, sinkName, location, report, target);
                }
                break;
        }
    }

    static string ToContextText(ExpressionValidationContext context)
    {
        return context switch
        {
            ExpressionValidationContext.Workflow => "workflow",
            ExpressionValidationContext.Job => "job",
            ExpressionValidationContext.Step => "step",
            _ => "unknown",
        };
    }
    static bool TryFindEmbeddedExpression(
        ReadOnlySpan<byte> value,
        int searchStart,
        out int bodyStart,
        out int bodyLength,
        out int nextSearchStart)
    {
        bodyStart = 0;
        bodyLength = 0;
        nextSearchStart = 0;

        if ((uint)searchStart >= (uint)value.Length)
        {
            return false;
        }

        var markerOffset = value[searchStart..].IndexOf("${{"u8);
        if (markerOffset < 0)
        {
            return false;
        }

        bodyStart = searchStart + markerOffset + 3;
        var closeOffset = value[bodyStart..].IndexOf("}}"u8);
        if (closeOffset < 0)
        {
            return false;
        }

        bodyLength = closeOffset;
        nextSearchStart = bodyStart + closeOffset + 2;
        return true;
    }
}
