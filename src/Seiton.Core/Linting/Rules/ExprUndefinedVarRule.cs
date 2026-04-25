using System.Text;
using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

using static Seiton.Core.Parsing.ExpressionScanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>Flags expressions that reference undefined context variables (e.g. <c>steps.missing-id.outputs.x</c>).</summary>
public sealed class ExprUndefinedVarRule() : RuleBase(RuleId.ExprUndefinedVar)
{
    public override string Name => "Expr Undefined Var Rule";

    // per-workflow and per-job dynamic context type overrides.
    // These replace the loose static types for steps/matrix/needs/inputs with strict,
    // job-specific types so that property-access errors can be detected.
    private Workflow? _currentWorkflow;
    private (byte[] NameUtf8, ExprType Type) _inputsOverride;
    private (byte[] NameUtf8, ExprType Type) _secretsOverride;
    // Reusable fixed-size override arrays to avoid per-job allocation
    private readonly (byte[] NameUtf8, ExprType Type)[] _jobScopeOverrides = new (byte[], ExprType)[4];
    private readonly (byte[] NameUtf8, ExprType Type)[] _stepScopeOverrides = new (byte[], ExprType)[5];
    private bool _hasOverrides;
    private readonly List<Diagnostic> _propertyDiagnostics = new();
    // Per-job state for incremental step override building
    private IReadOnlyList<Step>? _currentJobSteps;
    private int _currentStepIndex;
    // Local action output resolver for building strict step output types
    private LocalActionOutputResolver? _localActionOutputResolver;
    private Func<ReadOnlyMemory<byte>, string[]?>? _localActionOutputResolverFunc;

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        _currentWorkflow = workflow;
        _inputsOverride = DynamicContextTypeBuilder.BuildInputsOverride(workflow.On, Config.Utf8Yaml);
        _secretsOverride = DynamicContextTypeBuilder.BuildSecretsOverride(workflow.On, Config.Utf8Yaml);
        _hasOverrides = false;

        if (!string.IsNullOrEmpty(Config.FilePath) && Path.IsPathFullyQualified(Config.FilePath))
        {
            _localActionOutputResolver = new LocalActionOutputResolver(Config.FilePath);
            _localActionOutputResolverFunc = mem => _localActionOutputResolver.ResolveOutputNames(mem.Span);
        }
        else
        {
            _localActionOutputResolver = null;
            _localActionOutputResolverFunc = null;
        }
    }

    public override void VisitWorkflowPost(Workflow workflow)
    {
        if (Config.Utf8Yaml is null || _currentWorkflow is null)
        {
            return;
        }

        // Validate workflow_call output value expressions against jobs context
        for (var i = 0; i < workflow.On.Count; i++)
        {
            if (workflow.On[i] is not WorkflowCallEvent { Outputs: { Count: > 0 } outputs })
            {
                continue;
            }

            var jobsOverride = DynamicContextTypeBuilder.BuildJobsOverride(
                _currentWorkflow.Jobs, Config.Utf8Yaml);
            var overrides = new (byte[], ExprType)[]
            {
                jobsOverride,
                _inputsOverride,
            };

            foreach (var pair in outputs)
            {
                var output = pair.Value;
                if (!output.Value.HasValue)
                {
                    continue;
                }

                var value = Arena.GetStringValue(output.Value);
                if (value.Length == 0)
                {
                    continue;
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

                    // Validate context availability
                    var parseResult = Config.ParseExpression(expression);
                    if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
                    {
                        continue;
                    }

                    // Validate property access against jobs context
                    var propertyDiagnostics = _propertyDiagnostics;
                    propertyDiagnostics.Clear();
                    ExpressionSemanticAnalyzer.ValidateDynamicPropertyAccessInline(
                        parseResult, expression, Arena.GetStringRange(output.Value), overrides, propertyDiagnostics);
                    for (var d = 0; d < propertyDiagnostics.Count; d++)
                    {
                        var diag = propertyDiagnostics[d];
                        AddWorkflowError(workflow, diag.Message, diag.Location);
                    }
                }
            }
        }
    }

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null)
        {
            _hasOverrides = false;
            return;
        }

        var yaml = Config.Utf8Yaml;
        var matrixOverride = DynamicContextTypeBuilder.BuildMatrixOverride(job.Strategy?.Matrix, Arena, yaml);
        var needsOverride = DynamicContextTypeBuilder.BuildNeedsOverride(
            job.Needs,
            _currentWorkflow?.Jobs ?? default,
            Arena,
            yaml);

        // Store job steps for incremental step override building in VisitStep
        _currentJobSteps = job.Steps;
        _currentStepIndex = 0;

        // job scope: matrix, needs, inputs, secrets available (steps is NOT available in job scope)
        _jobScopeOverrides[0] = matrixOverride;
        _jobScopeOverrides[1] = needsOverride;
        _jobScopeOverrides[2] = _inputsOverride;
        _jobScopeOverrides[3] = _secretsOverride;
        // step scope: initialize with empty steps (will be rebuilt per-step in VisitStep)
        _stepScopeOverrides[0] = DynamicContextTypeBuilder.BuildStepsOverride(job.Steps, Arena, yaml, maxStepIndex: 0, _localActionOutputResolverFunc);
        _stepScopeOverrides[1] = matrixOverride;
        _stepScopeOverrides[2] = needsOverride;
        _stepScopeOverrides[3] = _inputsOverride;
        _stepScopeOverrides[4] = _secretsOverride;
        _hasOverrides = true;

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

        // Rebuild steps override to include only steps defined before the current one
        if (_hasOverrides && _currentJobSteps is not null)
        {
            _stepScopeOverrides[0] = DynamicContextTypeBuilder.BuildStepsOverride(
                _currentJobSteps, Arena, Config.Utf8Yaml, maxStepIndex: _currentStepIndex, _localActionOutputResolverFunc);
            _currentStepIndex++;
        }

        CheckNode(step.If, ExpressionValidationContext.Step, "step.if", static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        CheckEnv(step.Env, ExpressionValidationContext.Step, "step.env", static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        if (step.Exec is ExecRun run)
        {
            CheckNode(run.Run, ExpressionValidationContext.Step, "step.run", static (rule, message, location, targetStep) =>
                rule.AddStepError(targetStep, message, location), step);
        }
        else if (step.Exec is ExecAction action && action.Inputs is { Count: > 0 })
        {
            foreach (var pair in action.Inputs.Value)
            {
                var inputName = Decode(pair.Key);
                CheckNode(pair.Value, ExpressionValidationContext.Step, $"step.with.{inputName}", static (rule, message, location, targetStep) =>
                    rule.AddStepError(targetStep, message, location), step);
            }
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

        // When env is a single expression (${{ expr }}), check that it resolves to object type
        ValidateEnvMappingType(env.Expression, context, report, target);

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

        var hasEmbeddedExpression = ExpressionScanHelpers.ContainsExpressionMarker(value);
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

            // Template type check: object/array/null interpolated in ${{ }} outside if conditions
            if (!parseWholeValue)
            {
                ValidateTemplateType(expression, Arena.GetStringRange(node), context, report, target);
            }
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

        // also validate property access against dynamic context types
        if (!_hasOverrides)
        {
            return;
        }

        var overrides = context == ExpressionValidationContext.Step ? _stepScopeOverrides : _jobScopeOverrides;

        var propertyDiagnostics = _propertyDiagnostics;
        propertyDiagnostics.Clear();
        ExpressionSemanticAnalyzer.ValidateDynamicPropertyAccessInline(
            parseResult, expression, location, overrides, propertyDiagnostics);
        for (var i = 0; i < propertyDiagnostics.Count; i++)
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

    private void ValidateTemplateType<TTarget>(
        ReadOnlySpan<byte> expression,
        TextRange location,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        var parseResult = Config.ParseExpression(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return;
        }

        Diagnostic? diag;
        if (_hasOverrides)
        {
            var overrides = context == ExpressionValidationContext.Step ? _stepScopeOverrides : _jobScopeOverrides;
            diag = ExpressionSemanticAnalyzer.CheckTemplateTypeWithOverrides(parseResult, expression, location, overrides);
        }
        else
        {
            diag = ExpressionSemanticAnalyzer.CheckTemplateType(parseResult, expression, location);
        }

        if (diag is { } d)
        {
            report(this, d.Message, d.Location, target);
        }
    }

    private void ValidateEnvMappingType<TTarget>(
        StringNodeId envExpression,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!envExpression.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var value = Arena.GetStringValue(envExpression);
        if (value.Length == 0)
        {
            return;
        }

        // Extract the sole ${{ expr }} body
        if (!TryExtractExpressionBody(value, out var body))
        {
            return;
        }

        var parseResult = Config.ParseExpression(body);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return;
        }

        var overrides = _hasOverrides
            ? (context == ExpressionValidationContext.Step ? _stepScopeOverrides : _jobScopeOverrides)
            : null;

        var diag = ExpressionSemanticAnalyzer.CheckEnvMappingType(
            parseResult, body, Arena.GetStringRange(envExpression), overrides);
        if (diag is { } d)
        {
            report(this, d.Message, d.Location, target);
        }
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
