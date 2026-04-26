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

        // Workflow-level field checks
        CheckNode(workflow.RunName, ExpressionValidationContext.RunName, "run-name", static (rule, message, location, w) =>
            rule.AddWorkflowError(w, message, location), workflow);

        CheckEnv(workflow.Env, ExpressionValidationContext.Env, "env", static (rule, message, location, w) =>
            rule.AddWorkflowError(w, message, location), workflow);

        if (workflow.Concurrency is { } concurrency)
        {
            CheckNode(concurrency.Group, ExpressionValidationContext.Concurrency, "concurrency.group", static (rule, message, location, w) =>
                rule.AddWorkflowError(w, message, location), workflow);
        }

        if (workflow.Defaults?.Run is { } defaultsRun)
        {
            CheckNode(defaultsRun.Shell, ExpressionValidationContext.DefaultsRunShell, "defaults.run.shell", static (rule, message, location, w) =>
                rule.AddWorkflowError(w, message, location), workflow);
            CheckNode(defaultsRun.WorkingDirectory, ExpressionValidationContext.DefaultsRunShell, "defaults.run.working-directory", static (rule, message, location, w) =>
                rule.AddWorkflowError(w, message, location), workflow);
        }
    }

    public override void VisitEvent(Event ev)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        if (ev is WorkflowCallEvent wce)
        {
            if (wce.Inputs is { } inputs)
            {
                foreach (var input in inputs)
                {
                    CheckNode(input.Default, ExpressionValidationContext.WorkflowCallInputsDefault, "on.workflow_call.inputs.default", static (rule, message, location, e) =>
                        rule.AddWorkflowError(rule._currentWorkflow!, message, location), ev);
                }
            }
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

        CheckNode(job.If, ExpressionValidationContext.JobIf, "job.if", static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        CheckEnv(job.Env, ExpressionValidationContext.JobEnv, "job.env", static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        CheckNode(job.Name, ExpressionValidationContext.JobName, "job.name", static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        CheckNode(job.TimeoutMinutes, ExpressionValidationContext.JobTimeoutMinutes, "job.timeout-minutes", static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        CheckNode(job.ContinueOnError, ExpressionValidationContext.JobContinueOnError, "job.continue-on-error", static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        // job.runs-on
        if (job.RunsOn is { } runsOn)
        {
            CheckNode(runsOn.LabelsExpr, ExpressionValidationContext.JobRunsOn, "job.runs-on", static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
            if (runsOn.Labels is { } labels)
            {
                for (var li = 0; li < labels.Length; li++)
                {
                    CheckNode(labels[li], ExpressionValidationContext.JobRunsOn, "job.runs-on", static (rule, message, location, targetJob) =>
                        rule.AddJobError(targetJob, message, location), job);
                }
            }
            CheckNode(runsOn.Group, ExpressionValidationContext.JobRunsOn, "job.runs-on.group", static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.concurrency
        if (job.Concurrency is { } jobConcurrency)
        {
            CheckNode(jobConcurrency.Group, ExpressionValidationContext.JobConcurrency, "job.concurrency.group", static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.environment
        if (job.Environment is { } environment)
        {
            CheckNode(environment.Name, ExpressionValidationContext.JobEnvironment, "job.environment.name", static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
            CheckNode(environment.Url, ExpressionValidationContext.JobEnvironmentUrl, "job.environment.url", static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.defaults.run
        if (job.Defaults?.Run is { } jobDefaultsRun)
        {
            CheckNode(jobDefaultsRun.Shell, ExpressionValidationContext.JobDefaultsRun, "job.defaults.run.shell", static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
            CheckNode(jobDefaultsRun.WorkingDirectory, ExpressionValidationContext.JobDefaultsRun, "job.defaults.run.working-directory", static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.outputs
        if (job.Outputs is { Count: > 0 } outputs)
        {
            foreach (var pair in outputs)
            {
                CheckNode(pair.Value, ExpressionValidationContext.JobOutputs, "job.outputs", static (rule, message, location, targetJob) =>
                    rule.AddJobError(targetJob, message, location), job);
            }
        }

        // job.strategy
        CheckStrategy(job.Strategy, job);

        // job.container
        CheckContainer(job.Container, ExpressionValidationContext.JobContainerImage, ExpressionValidationContext.JobContainerCredentials, ExpressionValidationContext.JobContainerEnv, ExpressionValidationContext.JobContainer, job);

        // job.services
        CheckServices(job.Services, job);

        // job.secrets (reusable workflow call)
        var callSecrets = job.WorkflowCall?.Secrets;
        if (callSecrets is { Count: > 0 })
        {
            foreach (var pair in callSecrets.Value)
            {
                CheckNode(pair.Value.Value, ExpressionValidationContext.JobSecrets, "job.secrets", static (rule, message, location, targetJob) =>
                    rule.AddJobError(targetJob, message, location), job);
            }
        }

        var callInputs = job.WorkflowCall?.Inputs;
        if (callInputs is null || callInputs.Value.Count == 0)
        {
            return;
        }

        foreach (var pair in callInputs.Value)
        {
            var input = pair.Value;
            var inputName = Decode(Arena.GetStringSlice(input.Name));
            CheckNode(input.Value, ExpressionValidationContext.JobWith, $"job.with.{inputName}", static (rule, message, location, targetJob) =>
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

        CheckNode(step.If, ExpressionValidationContext.StepIf, "step.if", static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        CheckEnv(step.Env, ExpressionValidationContext.StepEnv, "step.env", static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        CheckNode(step.Name, ExpressionValidationContext.StepName, "step.name", static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        CheckNode(step.Id, ExpressionValidationContext.StepId, "step.id", static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        CheckNode(step.ContinueOnError, ExpressionValidationContext.StepContinueOnError, "step.continue-on-error", static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        CheckNode(step.TimeoutMinutes, ExpressionValidationContext.StepTimeoutMinutes, "step.timeout-minutes", static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        if (step.Exec is ExecRun run)
        {
            CheckNode(run.Run, ExpressionValidationContext.StepRun, "step.run", static (rule, message, location, targetStep) =>
                rule.AddStepError(targetStep, message, location), step);
            CheckNode(run.Shell, ExpressionValidationContext.StepShell, "step.shell", static (rule, message, location, targetStep) =>
                rule.AddStepError(targetStep, message, location), step);
            CheckNode(run.WorkingDirectory, ExpressionValidationContext.StepWorkingDirectory, "step.working-directory", static (rule, message, location, targetStep) =>
                rule.AddStepError(targetStep, message, location), step);
        }
        else if (step.Exec is ExecAction action && action.Inputs is { Count: > 0 })
        {
            foreach (var pair in action.Inputs.Value)
            {
                var inputName = Decode(pair.Key);
                CheckNode(pair.Value, ExpressionValidationContext.StepWith, $"step.with.{inputName}", static (rule, message, location, targetStep) =>
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
        FloatNodeId node,
        ExpressionValidationContext context,
        string sinkName,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue) return;
        CheckNode(Arena.GetFloatExpression(node), context, sinkName, report, target);
    }

    private void CheckNode<TTarget>(
        BoolNodeId node,
        ExpressionValidationContext context,
        string sinkName,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue) return;
        CheckNode(Arena.GetBoolExpression(node), context, sinkName, report, target);
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

        var overrides = Availability.IsStepLevel(context) ? _stepScopeOverrides : _jobScopeOverrides;

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
                if (IsBuiltinContext(rootName))
                {
                    var availableText = Availability.FormatAvailableContexts(context);
                    report(
                        this,
                        $"context \"{rootNameText}\" is not allowed here. {availableText}",
                        location,
                        target);
                }
                else
                {
                    report(
                        this,
                        $"context \"{rootNameText}\" is not allowed here. undefined context \"{rootNameText}\"",
                        location,
                        target);
                }
            }
        }

        // Special function availability checks
        if (node.Kind == ExpressionNodeKind.FunctionCall && node.Left >= 0 && node.Left < nodes.Length)
        {
            var callee = nodes[node.Left];
            if (callee.Kind == ExpressionNodeKind.Identifier)
            {
                var funcName = callee.Token.AsSpan(expression);

                // Status check functions: only in if conditions
                var isIfContext = context is ExpressionValidationContext.JobIf or ExpressionValidationContext.StepIf;
                if (!isIfContext && IsStatusCheckFunction(funcName))
                {
                    var funcNameText = Encoding.UTF8.GetString(funcName);
                    report(
                        this,
                        $"function \"{funcNameText}\" is not allowed here. \"{funcNameText}\" is only available in \"if\" conditions of jobs and steps",
                        location,
                        target);
                }

                // hashFiles: only at step level (not job.if)
                if (IsHashFilesFunction(funcName) && !Availability.IsStepLevel(context))
                {
                    report(
                        this,
                        $"function \"hashFiles\" is not allowed here. \"hashFiles\" is only available in step-level expressions",
                        location,
                        target);
                }
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

    private static string ToContextText(ExpressionValidationContext context) => Availability.GetLintCategoryText(context);

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
            var overrides = Availability.IsStepLevel(context) ? _stepScopeOverrides : _jobScopeOverrides;
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
            ? (Availability.IsStepLevel(context) ? _stepScopeOverrides : _jobScopeOverrides)
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

    private void CheckStrategy(Strategy? strategy, Job job)
    {
        if (strategy is null) return;

        CheckNode(strategy.FailFast, ExpressionValidationContext.JobStrategy, "job.strategy.fail-fast", static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);
        CheckNode(strategy.MaxParallel, ExpressionValidationContext.JobStrategy, "job.strategy.max-parallel", static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);

        if (strategy.Matrix is { } matrix)
        {
            CheckNode(Arena.GetStringExpression(matrix.Expression), ExpressionValidationContext.JobStrategy, "job.strategy.matrix", static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);

            if (matrix.Rows is { Count: > 0 })
            {
                foreach (var pair in matrix.Rows)
                {
                    CheckMatrixValues(pair.Value.Values, ExpressionValidationContext.JobStrategy, "job.strategy.matrix", job);
                    CheckNode(Arena.GetStringExpression(pair.Value.Expression), ExpressionValidationContext.JobStrategy, "job.strategy.matrix", static (rule, message, location, j) =>
                        rule.AddJobError(j, message, location), job);
                }
            }
            if (matrix.Include is { Count: > 0 })
            {
                foreach (var combo in matrix.Include)
                {
                    CheckNode(Arena.GetStringExpression(combo.Expression), ExpressionValidationContext.JobStrategy, "job.strategy.include", static (rule, message, location, j) =>
                        rule.AddJobError(j, message, location), job);
                    CheckMatrixCombinationEntries(combo.Entries, ExpressionValidationContext.JobStrategy, "job.strategy.include", job);
                }
            }
            if (matrix.Exclude is { Count: > 0 })
            {
                foreach (var combo in matrix.Exclude)
                {
                    CheckNode(Arena.GetStringExpression(combo.Expression), ExpressionValidationContext.JobStrategy, "job.strategy.exclude", static (rule, message, location, j) =>
                        rule.AddJobError(j, message, location), job);
                    CheckMatrixCombinationEntries(combo.Entries, ExpressionValidationContext.JobStrategy, "job.strategy.exclude", job);
                }
            }
        }
    }

    private void CheckMatrixValues(IReadOnlyList<RawYamlValue>? values, ExpressionValidationContext context, string sinkName, Job job)
    {
        if (values is null) return;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is RawYamlString str)
            {
                CheckNode(str.Value, context, sinkName, static (rule, message, location, j) =>
                    rule.AddJobError(j, message, location), job);
            }
        }
    }

    private void CheckMatrixCombinationEntries(IReadOnlyList<SliceMap<RawYamlValue>>? entries, ExpressionValidationContext context, string sinkName, Job job)
    {
        if (entries is null) return;
        for (var i = 0; i < entries.Count; i++)
        {
            foreach (var pair in entries[i])
            {
                if (pair.Value is RawYamlString str)
                {
                    CheckNode(str.Value, context, sinkName, static (rule, message, location, j) =>
                        rule.AddJobError(j, message, location), job);
                }
            }
        }
    }

    private void CheckContainer(Container? container, ExpressionValidationContext imageCtx, ExpressionValidationContext credentialsCtx, ExpressionValidationContext envCtx, ExpressionValidationContext optionsCtx, Job job)
    {
        if (container is null) return;

        CheckNode(container.Image, imageCtx, "job.container.image", static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);
        CheckNode(container.Options, optionsCtx, "job.container.options", static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);
        CheckEnv(container.Env, envCtx, "job.container.env", static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);

        if (container.Credentials is { } creds)
        {
            CheckNode(creds.Username, credentialsCtx, "job.container.credentials.username", static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
            CheckNode(creds.Password, credentialsCtx, "job.container.credentials.password", static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
            CheckNode(Arena.GetStringExpression(creds.Expression), credentialsCtx, "job.container.credentials", static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
        }
    }

    private void CheckServices(Services? services, Job job)
    {
        if (services is null) return;

        CheckNode(Arena.GetStringExpression(services.Expression), ExpressionValidationContext.JobServices, "job.services", static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);

        if (services.ServiceMap is not { Count: > 0 }) return;

        foreach (var pair in services.ServiceMap)
        {
            var svc = pair.Value;
            var svcContainer = svc.Container;
            if (svcContainer is null) continue;

            CheckNode(svcContainer.Image, ExpressionValidationContext.JobServices, "job.services.image", static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
            CheckNode(svcContainer.Options, ExpressionValidationContext.JobServices, "job.services.options", static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
            CheckEnv(svcContainer.Env, ExpressionValidationContext.JobServicesEnv, "job.services.env", static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);

            if (svcContainer.Credentials is { } svcCreds)
            {
                CheckNode(svcCreds.Username, ExpressionValidationContext.JobServicesCredentials, "job.services.credentials.username", static (rule, message, location, j) =>
                    rule.AddJobError(j, message, location), job);
                CheckNode(svcCreds.Password, ExpressionValidationContext.JobServicesCredentials, "job.services.credentials.password", static (rule, message, location, j) =>
                    rule.AddJobError(j, message, location), job);
                CheckNode(Arena.GetStringExpression(svcCreds.Expression), ExpressionValidationContext.JobServicesCredentials, "job.services.credentials", static (rule, message, location, j) =>
                    rule.AddJobError(j, message, location), job);
            }
        }
    }

    private void CheckNode<TTarget>(
        IntNodeId node,
        ExpressionValidationContext context,
        string sinkName,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue) return;
        CheckNode(Arena.GetIntExpression(node), context, sinkName, report, target);
    }

    private static bool IsStatusCheckFunction(ReadOnlySpan<byte> nameUtf8)
    {
        return EqualsAsciiIgnoreCase(nameUtf8, "success"u8)
            || EqualsAsciiIgnoreCase(nameUtf8, "failure"u8)
            || EqualsAsciiIgnoreCase(nameUtf8, "cancelled"u8)
            || EqualsAsciiIgnoreCase(nameUtf8, "always"u8);
    }

    private static bool IsHashFilesFunction(ReadOnlySpan<byte> nameUtf8)
    {
        return EqualsAsciiIgnoreCase(nameUtf8, "hashfiles"u8);
    }

    private static bool IsBuiltinContext(ReadOnlySpan<byte> nameUtf8)
    {
        var builtins = Generated.ContextTypes.BuiltinContextTypes;
        for (var i = 0; i < builtins.Length; i++)
        {
            if (EqualsAsciiIgnoreCase(nameUtf8, builtins[i].NameUtf8))
            {
                return true;
            }
        }
        return false;
    }
}
