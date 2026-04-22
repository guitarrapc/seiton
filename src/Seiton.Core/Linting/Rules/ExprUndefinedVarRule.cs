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

    // Phase 2: per-workflow and per-job dynamic context type overrides.
    // These replace the loose static types for steps/matrix/needs/inputs with strict,
    // job-specific types so that property-access errors can be detected.
    private Workflow? _currentWorkflow;
    private (byte[] NameUtf8, ExprType Type) _inputsOverride;
    private (byte[] NameUtf8, ExprType Type)[]? _jobScopeOverrides;
    private (byte[] NameUtf8, ExprType Type)[]? _stepScopeOverrides;

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        _currentWorkflow = workflow;
        _inputsOverride = DynamicContextTypeBuilder.BuildInputsOverride(workflow.On, Config.Utf8Yaml);
        _jobScopeOverrides = null;
        _stepScopeOverrides = null;
    }

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null)
        {
            _jobScopeOverrides = null;
            _stepScopeOverrides = null;
            return;
        }

        var yaml = Config.Utf8Yaml;
        var matrixOverride = DynamicContextTypeBuilder.BuildMatrixOverride(job.Strategy?.Matrix, Arena, yaml);
        var needsOverride = DynamicContextTypeBuilder.BuildNeedsOverride(
            job.Needs,
            _currentWorkflow?.Jobs ?? default,
            Arena,
            yaml);
        var stepsOverride = DynamicContextTypeBuilder.BuildStepsOverride(job.Steps, Arena, yaml);

        // job scope: matrix, needs, inputs available (steps is NOT available in job scope)
        _jobScopeOverrides = [matrixOverride, needsOverride, _inputsOverride];
        // step scope: also includes steps
        _stepScopeOverrides = [stepsOverride, matrixOverride, needsOverride, _inputsOverride];

        CheckNode(job.If, ExpressionValidationContext.Job, "job.if", static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        CheckEnv(job.Env, ExpressionValidationContext.Job, "job.env", static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        var callInputs = job.WorkflowCall?.Inputs;
        if (callInputs is null || callInputs.Value.Count == 0)
        {
            return;
        }

        foreach (var pair in callInputs.Value)
        {
            var input = pair.Value;
            var inputName = Decode(Arena.GetStringSlice(input.Name));
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

        if (step.Exec is not ExecAction action || action.Inputs is null || action.Inputs.Value.Count == 0)
        {
            return;
        }

        foreach (var pair in action.Inputs.Value)
        {
            var inputName = Decode(pair.Key);
            CheckNode(pair.Value, ExpressionValidationContext.Step, $"step.with.{inputName}", static (rule, message, location, targetStep) =>
                rule.AddStepError(targetStep, message, location), step);
        }
    }

    private void CheckEnv<TTarget>(
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

        CheckNode(Arena.GetStringExpression(env.Expression), context, sinkName, report, target);

        var vars = env.Vars;
        if (vars is null || vars.Value.Count == 0)
        {
            return;
        }

        foreach (var pair in vars.Value)
        {
            var envVar = pair.Value;
            var keyName = Decode(Arena.GetStringSlice(envVar.Name));
            CheckNode(envVar.Value, context, $"{sinkName}.{keyName}", report, target);
        }
    }

    private void CheckNode<TTarget>(
        StringNodeId node,
        ExpressionValidationContext context,
        string sinkName,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var value = Arena.GetStringValue(node);
        if (value.Length == 0)
        {
            return;
        }

        var hasEmbeddedExpression = value.IndexOf("${{"u8) >= 0;
        var parseWholeValue = sinkName.EndsWith(".if", StringComparison.Ordinal);

        if (parseWholeValue && !hasEmbeddedExpression)
        {
            ValidateExpression(value, context, sinkName, Arena.GetStringRange(node), report, target);
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

            ValidateExpression(expression, context, sinkName, Arena.GetStringRange(node), report, target);
        }
    }

    private void ValidateExpression<TTarget>(
        ReadOnlySpan<byte> expression,
        ExpressionValidationContext context,
        string sinkName,
        TextRange location,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        var parseResult = Config.ParseExpression(expression);
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

        // Phase 2: also validate property access against dynamic context types
        var overrides = context == ExpressionValidationContext.Step ? _stepScopeOverrides : _jobScopeOverrides;
        if (overrides is null || overrides.Length == 0)
        {
            return;
        }

        var propertyDiagnostics = ExpressionSemanticAnalyzer.ValidateDynamicPropertyAccess(
            parseResult, expression, location, overrides);
        for (var i = 0; i < propertyDiagnostics.Length; i++)
        {
            var d = propertyDiagnostics[i];
            report(this, d.Message, d.Location, target);
        }
    }

    private void VisitExpressionNode<TTarget>(
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

    private static string ToContextText(ExpressionValidationContext context)
    {
        return context switch
        {
            ExpressionValidationContext.Workflow => "workflow",
            ExpressionValidationContext.Job => "job",
            ExpressionValidationContext.Step => "step",
            _ => "unknown",
        };
    }
    private static bool TryFindEmbeddedExpression(
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
