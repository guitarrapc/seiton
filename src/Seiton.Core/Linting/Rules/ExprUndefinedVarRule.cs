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
    private (byte[] NameUtf8, ExprType Type) _githubOverride;
    // Cache for BuildGithubOverride: avoid rebuilding ~40-entry Dictionary per lint run
    private (byte[] NameUtf8, ExprType Type) _cachedGithubOverride;
    private byte[]? _cachedGithubYamlRef;
    private int _cachedGithubEventCount;
    // Reusable fixed-size override arrays to avoid per-job allocation
    private readonly (byte[] NameUtf8, ExprType Type)[] _jobScopeOverrides = new (byte[], ExprType)[5];
    private readonly (byte[] NameUtf8, ExprType Type)[] _stepScopeOverrides = new (byte[], ExprType)[6];
    private bool _hasOverrides;
    private readonly List<Diagnostic> _propertyDiagnostics = new();
    // Per-job state for incremental step override building
    private IReadOnlyList<Step>? _currentJobSteps;
    private int _currentStepIndex;
    // Reusable dictionary for BuildStepsOverrideInto (avoids per-step allocation)
    private readonly Dictionary<Utf8String, ExprType> _stepsOverrideProps = new();
    // Reusable dictionaries for BuildMatrixOverrideInto / BuildNeedsOverrideInto (avoids per-job allocation)
    private readonly Dictionary<Utf8String, ExprType> _matrixOverrideProps = new();
    private readonly Dictionary<Utf8String, ExprType> _needsOverrideProps = new();
    // Local action output resolver for building strict step output types
    private LocalActionOutputResolver? _localActionOutputResolver;
    private Func<ReadOnlyMemory<byte>, string[]?>? _localActionOutputResolverFunc;
    // Local reusable workflow output resolver for building strict needs output types
    private LocalReusableWorkflowOutputResolver? _localReusableOutputResolver;
    private Func<ReadOnlyMemory<byte>, string[]?>? _localReusableOutputResolverFunc;

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        _currentWorkflow = workflow;
        _inputsOverride = DynamicContextTypeBuilder.BuildInputsOverride(workflow.On, Config.Utf8Yaml);
        _secretsOverride = DynamicContextTypeBuilder.BuildSecretsOverride(workflow.On, Config.Utf8Yaml);

        // Cache github override: only rebuild when source file or event count changes
        if (ReferenceEquals(Config.Utf8Yaml, _cachedGithubYamlRef) && workflow.On.Count == _cachedGithubEventCount)
        {
            _githubOverride = _cachedGithubOverride;
        }
        else
        {
            _githubOverride = DynamicContextTypeBuilder.BuildGithubOverride(workflow.On, Arena, Config.Utf8Yaml);
            _cachedGithubOverride = _githubOverride;
            _cachedGithubYamlRef = Config.Utf8Yaml;
            _cachedGithubEventCount = workflow.On.Count;
        }

        _hasOverrides = false;

        if (!string.IsNullOrEmpty(Config.FilePath) && Path.IsPathFullyQualified(Config.FilePath))
        {
            _localActionOutputResolver = new LocalActionOutputResolver(Config.FilePath);
            _localActionOutputResolverFunc = mem => _localActionOutputResolver.ResolveOutputNames(mem.Span);
            _localReusableOutputResolver = new LocalReusableWorkflowOutputResolver(Config.FilePath);
            _localReusableOutputResolverFunc = mem => _localReusableOutputResolver.ResolveOutputNames(mem.Span);
        }
        else
        {
            _localActionOutputResolver = null;
            _localActionOutputResolverFunc = null;
            _localReusableOutputResolver = null;
            _localReusableOutputResolverFunc = null;
        }

        // Workflow-level field checks
        CheckNode(workflow.RunName, ExpressionValidationContext.RunName, static (rule, message, location, w) =>
            rule.AddWorkflowError(w, message, location), workflow);

        CheckEnv(workflow.Env, ExpressionValidationContext.Env, static (rule, message, location, w) =>
            rule.AddWorkflowError(w, message, location), workflow);

        if (workflow.Concurrency is { } concurrency)
        {
            CheckNode(concurrency.Group, ExpressionValidationContext.Concurrency, static (rule, message, location, w) =>
                rule.AddWorkflowError(w, message, location), workflow);
        }

        if (workflow.Defaults?.Run is { } defaultsRun)
        {
            CheckNode(defaultsRun.Shell, ExpressionValidationContext.DefaultsRunShell, static (rule, message, location, w) =>
                rule.AddWorkflowError(w, message, location), workflow);
            CheckNode(defaultsRun.WorkingDirectory, ExpressionValidationContext.DefaultsRunShell, static (rule, message, location, w) =>
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
                for (var idx = 0; idx < inputs.Count; idx++)
                {
                    var input = inputs[idx];
                    if (!input.Default.HasValue)
                    {
                        continue;
                    }

                    // Build incremental inputs override: only inputs defined before current index
                    var incrementalInputsOverride = DynamicContextTypeBuilder.BuildWorkflowCallInputsOverrideUpTo(inputs, idx);

                    CheckNodeWithOverrides(
                        input.Default,
                        ExpressionValidationContext.WorkflowCallInputsDefault,
                        [incrementalInputsOverride],
                        static (rule, message, location, e) =>
                            rule.AddWorkflowError(rule._currentWorkflow!, message, location),
                        ev);

                    // Type check: inferred expression type vs declared input type
                    if (input.Type is WorkflowCallInputType.Boolean or WorkflowCallInputType.Number)
                    {
                        ValidateInputDefaultType(input, [incrementalInputsOverride]);
                    }
                }
            }
        }
    }

    private void ValidateInputDefaultType(WorkflowCallEventInput input, (byte[] NameUtf8, ExprType Type)[] overrides)
    {
        var value = Arena.GetStringValue(input.Default);
        if (value.Length == 0)
        {
            return;
        }

        var searchStart = 0;
        if (!TryFindEmbeddedExpression(value, searchStart, out var bodyStart, out var bodyLength, out _))
        {
            return;
        }

        var expression = TrimAsciiWhiteSpace(value.Slice(bodyStart, bodyLength));
        if (expression.Length == 0)
        {
            return;
        }

        var parseResult = Config.ParseExpression(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return;
        }

        var inferredType = ExpressionSemanticAnalyzer.InferTypeWithOverrides(
            parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression, overrides);

        var expectedTypeName = input.Type switch
        {
            WorkflowCallInputType.Boolean => "bool",
            WorkflowCallInputType.Number => "number",
            _ => null,
        };

        if (expectedTypeName is null)
        {
            return;
        }

        // Only report when the inferred type is concrete and mismatched
        if (inferredType is AnyExprType or ObjectExprType or ArrayExprType or NullExprType)
        {
            return;
        }

        if (inferredType.TypeName == expectedTypeName)
        {
            return;
        }

        var inputName = Encoding.UTF8.GetString(input.Id.Span);
        var message = $"type of input \"{inputName}\" must be {expectedTypeName} but found type {inferredType.TypeName}";
        var location = Arena.GetStringRange(input.Default);
        AddWorkflowError(_currentWorkflow!, message, location);
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
                CheckNodeWithOverrides(
                    output.Value,
                    ExpressionValidationContext.WorkflowCallOutputsValue,
                    overrides,
                    static (rule, message, location, w) =>
                        rule.AddWorkflowError(w, message, location),
                    workflow);
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
        var matrixOverride = DynamicContextTypeBuilder.BuildMatrixOverrideInto(_matrixOverrideProps, job.Strategy?.Matrix, Arena, yaml);
        var needsOverride = DynamicContextTypeBuilder.BuildNeedsOverrideInto(
            _needsOverrideProps,
            job.Needs,
            _currentWorkflow?.Jobs ?? default,
            Arena,
            yaml,
            _localReusableOutputResolverFunc);

        // Store job steps for incremental step override building in VisitStep
        _currentJobSteps = job.Steps;
        _currentStepIndex = 0;

        // job scope: matrix, needs, inputs, secrets, github available (steps is NOT available in job scope)
        _jobScopeOverrides[0] = matrixOverride;
        _jobScopeOverrides[1] = needsOverride;
        _jobScopeOverrides[2] = _inputsOverride;
        _jobScopeOverrides[3] = _secretsOverride;
        _jobScopeOverrides[4] = _githubOverride;
        // step scope: initialize with empty steps (will be rebuilt per-step in VisitStep)
        _stepScopeOverrides[0] = DynamicContextTypeBuilder.BuildStepsOverrideInto(_stepsOverrideProps, job.Steps, Arena, yaml, maxStepIndex: 0, _localActionOutputResolverFunc);
        _stepScopeOverrides[1] = matrixOverride;
        _stepScopeOverrides[2] = needsOverride;
        _stepScopeOverrides[3] = _inputsOverride;
        _stepScopeOverrides[4] = _secretsOverride;
        _stepScopeOverrides[5] = _githubOverride;
        _hasOverrides = true;

        CheckNode(job.If, ExpressionValidationContext.JobIf, static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        CheckEnv(job.Env, ExpressionValidationContext.JobEnv, static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        CheckNode(job.Name, ExpressionValidationContext.JobName, static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        CheckNode(job.TimeoutMinutes, ExpressionValidationContext.JobTimeoutMinutes, static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        CheckNode(job.ContinueOnError, ExpressionValidationContext.JobContinueOnError, static (rule, message, location, targetJob) =>
            rule.AddJobError(targetJob, message, location), job);

        // job.runs-on
        if (job.RunsOn is { } runsOn)
        {
            CheckNode(runsOn.LabelsExpr, ExpressionValidationContext.JobRunsOn, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job, isRunsOnLabels: true);
            if (runsOn.Labels is { } labels)
            {
                for (var li = 0; li < labels.Length; li++)
                {
                    CheckNode(labels[li], ExpressionValidationContext.JobRunsOn, static (rule, message, location, targetJob) =>
                        rule.AddJobError(targetJob, message, location), job, isRunsOnLabels: true);
                }
            }
            CheckNode(runsOn.Group, ExpressionValidationContext.JobRunsOn, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.concurrency
        if (job.Concurrency is { } jobConcurrency)
        {
            CheckNode(jobConcurrency.Group, ExpressionValidationContext.JobConcurrency, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.environment
        if (job.Environment is { } environment)
        {
            CheckNode(environment.Name, ExpressionValidationContext.JobEnvironment, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
            CheckNode(environment.Url, ExpressionValidationContext.JobEnvironmentUrl, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.defaults.run
        if (job.Defaults?.Run is { } jobDefaultsRun)
        {
            CheckNode(jobDefaultsRun.Shell, ExpressionValidationContext.JobDefaultsRun, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
            CheckNode(jobDefaultsRun.WorkingDirectory, ExpressionValidationContext.JobDefaultsRun, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.outputs
        if (job.Outputs is { Count: > 0 } outputs)
        {
            foreach (var pair in outputs)
            {
                CheckNode(pair.Value, ExpressionValidationContext.JobOutputs, static (rule, message, location, targetJob) =>
                    rule.AddJobError(targetJob, message, location), job);
            }
        }

        // job.strategy
        CheckStrategy(job.Strategy, job);

        // job.container
        CheckContainer(job.Container, ExpressionValidationContext.JobContainerImage, ExpressionValidationContext.JobContainerCredentials, ExpressionValidationContext.JobContainerEnv, ExpressionValidationContext.JobContainer, job);

        // job.services
        CheckServices(job.Services, job);

        // job.snapshot.if
        if (job.Snapshot is { } snapshot)
        {
            CheckNode(snapshot.If, ExpressionValidationContext.JobSnapshotIf, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.secrets (reusable workflow call)
        var callSecrets = job.WorkflowCall?.Secrets;
        if (callSecrets is { Count: > 0 })
        {
            foreach (var pair in callSecrets.Value)
            {
                CheckNode(pair.Value.Value, ExpressionValidationContext.JobSecrets, static (rule, message, location, targetJob) =>
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
            CheckNode(input.Value, ExpressionValidationContext.JobWith, static (rule, message, location, targetJob) =>
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
            _stepScopeOverrides[0] = DynamicContextTypeBuilder.BuildStepsOverrideInto(
                _stepsOverrideProps, _currentJobSteps, Arena, Config.Utf8Yaml, maxStepIndex: _currentStepIndex, _localActionOutputResolverFunc);
            _currentStepIndex++;
        }

        CheckNode(step.If, ExpressionValidationContext.StepIf, static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        CheckEnv(step.Env, ExpressionValidationContext.StepEnv, static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        CheckNode(step.Name, ExpressionValidationContext.StepName, static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        CheckNode(step.Id, ExpressionValidationContext.StepId, static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        CheckNode(step.ContinueOnError, ExpressionValidationContext.StepContinueOnError, static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        CheckNode(step.TimeoutMinutes, ExpressionValidationContext.StepTimeoutMinutes, static (rule, message, location, targetStep) =>
            rule.AddStepError(targetStep, message, location), step);

        if (step.Exec is ExecRun run)
        {
            CheckNode(run.Run, ExpressionValidationContext.StepRun, static (rule, message, location, targetStep) =>
                rule.AddStepError(targetStep, message, location), step);
            CheckNode(run.Shell, ExpressionValidationContext.StepShell, static (rule, message, location, targetStep) =>
                rule.AddStepError(targetStep, message, location), step);
            CheckNode(run.WorkingDirectory, ExpressionValidationContext.StepWorkingDirectory, static (rule, message, location, targetStep) =>
                rule.AddStepError(targetStep, message, location), step);
        }
        else if (step.Exec is ExecAction action && action.Inputs is { Count: > 0 })
        {
            foreach (var pair in action.Inputs.Value)
            {
                CheckNode(pair.Value, ExpressionValidationContext.StepWith, static (rule, message, location, targetStep) =>
                    rule.AddStepError(targetStep, message, location), step);
            }
        }
    }

    private void CheckEnv<TTarget>(
        Env? env,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (env is null)
        {
            return;
        }

        CheckSectionExpression(env.Expression, context, report, target);

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
            CheckNode(envVar.Name, context, report, target);
            CheckNode(envVar.Value, context, report, target);
        }
    }

    private void CheckNode<TTarget>(
        FloatNodeId node,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue) return;
        CheckNode(Arena.GetFloatExpression(node), context, report, target);
    }

    private void CheckNode<TTarget>(
        BoolNodeId node,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue) return;
        CheckNode(Arena.GetBoolExpression(node), context, report, target);
    }

    private void CheckNode<TTarget>(
        StringNodeId node,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target,
        bool isRunsOnLabels = false)
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
        var parseWholeValue = context is ExpressionValidationContext.JobIf or ExpressionValidationContext.StepIf or ExpressionValidationContext.JobSnapshotIf;

        if (parseWholeValue && !hasEmbeddedExpression)
        {
            ValidateExpression(value, context, Arena.GetStringRange(node), report, target);
            return;
        }

        var nodeRange = Arena.GetStringRange(node);
        var searchStart = 0;
        while (TryFindEmbeddedExpression(value, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;
            var expression = TrimAsciiWhiteSpace(value.Slice(bodyStart, bodyLength));
            if (expression.Length == 0)
            {
                continue;
            }

            var exprLocation = ComputeExpressionLocation(nodeRange, value, bodyStart - 3);
            ValidateExpression(expression, context, exprLocation, report, target);

            // Template type check: object/array/null interpolated in ${{ }} outside if conditions
            if (!parseWholeValue)
            {
                ValidateTemplateType(expression, exprLocation, context, isRunsOnLabels, report, target);
            }
        }
    }

    /// <summary>
    /// Validates expressions in section-level ${{ }} forms (env, services, credentials, matrix).
    /// Only checks context availability — skips template type checking because these sections
    /// evaluate the expression result directly rather than interpolating into a string.
    /// </summary>
    private void CheckSectionExpression<TTarget>(
        StringNodeId node,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue || Config.Utf8Yaml is null) return;
        var value = Arena.GetStringValue(node);
        if (value.Length == 0) return;

        var nodeRange = Arena.GetStringRange(node);
        var searchStart = 0;
        while (TryFindEmbeddedExpression(value, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;
            var expression = TrimAsciiWhiteSpace(value.Slice(bodyStart, bodyLength));
            if (expression.Length == 0) continue;

            var exprLocation = ComputeExpressionLocation(nodeRange, value, bodyStart - 3);
            ValidateExpression(expression, context, exprLocation, report, target);
        }
    }

    private void ValidateExpression<TTarget>(
        ReadOnlySpan<byte> expression,
        ExpressionValidationContext context,
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

    /// <summary>
    /// Checks a string node with explicit overrides instead of the per-job overrides.
    /// Used for contexts like workflow_call input defaults where overrides are computed per-node.
    /// </summary>
    private void CheckNodeWithOverrides<TTarget>(
        StringNodeId node,
        ExpressionValidationContext context,
        (byte[] NameUtf8, ExprType Type)[] overrides,
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

        var nodeRange = Arena.GetStringRange(node);
        var searchStart = 0;
        while (TryFindEmbeddedExpression(value, searchStart, out var bodyStart, out var bodyLength, out var nextSearchStart))
        {
            searchStart = nextSearchStart;
            var expression = TrimAsciiWhiteSpace(value.Slice(bodyStart, bodyLength));
            if (expression.Length == 0)
            {
                continue;
            }

            var parseResult = Config.ParseExpression(expression);
            if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
            {
                continue;
            }

            var location = ComputeExpressionLocation(nodeRange, value, bodyStart - 3);

            // Context availability check
            VisitExpressionNode(
                parseResult.RootNode,
                parentId: -1,
                parseResult.Nodes,
                parseResult.Arguments,
                expression,
                context,
                location,
                report,
                target);

            // Property access check with explicit overrides
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
    }

    private void VisitExpressionNode<TTarget>(
        int nodeId,
        int parentId,
        ExpressionNode[] nodes,
        int[] arguments,
        ReadOnlySpan<byte> expression,
        ExpressionValidationContext context,
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
                var scopeText = FormatScopeName(context);
                if (IsBuiltinContext(rootName))
                {
                    var availableText = Availability.FormatAvailableContexts(context);
                    report(
                        this,
                        $"context \"{rootNameText}\" is not allowed here. {availableText}. called in {scopeText}",
                        location,
                        target);
                }
                else
                {
                    report(
                        this,
                        $"context \"{rootNameText}\" is not allowed here. undefined context \"{rootNameText}\". called in {scopeText}",
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
                var isIfContext = context is ExpressionValidationContext.JobIf or ExpressionValidationContext.StepIf or ExpressionValidationContext.JobSnapshotIf;
                if (!isIfContext && IsStatusCheckFunction(funcName))
                {
                    var funcNameText = Encoding.UTF8.GetString(funcName);
                    var scopeText = FormatScopeName(context);
                    report(
                        this,
                        $"function \"{funcNameText}\" is not allowed here. \"{funcNameText}\" is only available in \"if\" conditions of jobs and steps. called in {scopeText}",
                        location,
                        target);
                }

                // hashFiles: only at step level (not job.if)
                if (IsHashFilesFunction(funcName) && !Availability.IsStepLevel(context))
                {
                    var scopeText = FormatScopeName(context);
                    report(
                        this,
                        $"function \"hashFiles\" is not allowed here. \"hashFiles\" is only available in step-level expressions. called in {scopeText}",
                        location,
                        target);
                }
            }
        }

        switch (node.Kind)
        {
            case ExpressionNodeKind.Unary:
                VisitExpressionNode(node.Left, nodeId, nodes, arguments, expression, context, location, report, target);
                break;
            case ExpressionNodeKind.Binary:
                VisitExpressionNode(node.Left, nodeId, nodes, arguments, expression, context, location, report, target);
                VisitExpressionNode(node.Right, nodeId, nodes, arguments, expression, context, location, report, target);
                break;
            case ExpressionNodeKind.MemberAccess:
            case ExpressionNodeKind.WildcardAccess:
                VisitExpressionNode(node.Left, nodeId, nodes, arguments, expression, context, location, report, target);
                break;
            case ExpressionNodeKind.IndexAccess:
                VisitExpressionNode(node.Left, nodeId, nodes, arguments, expression, context, location, report, target);
                VisitExpressionNode(node.Right, nodeId, nodes, arguments, expression, context, location, report, target);
                break;
            case ExpressionNodeKind.FunctionCall:
                VisitExpressionNode(node.Left, nodeId, nodes, arguments, expression, context, location, report, target);
                for (var i = 0; i < node.ArgCount; i++)
                {
                    var argIndex = node.ArgStart + i;
                    if (argIndex < 0 || argIndex >= arguments.Length)
                    {
                        continue;
                    }

                    VisitExpressionNode(arguments[argIndex], nodeId, nodes, arguments, expression, context, location, report, target);
                }
                break;
        }
    }

    private static string ToContextText(ExpressionValidationContext context) => Availability.GetLintCategoryText(context);

    private void ValidateTemplateType<TTarget>(
        ReadOnlySpan<byte> expression,
        TextRange location,
        ExpressionValidationContext context,
        bool isRunsOnLabels,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        var parseResult = Config.ParseExpression(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return;
        }

        Diagnostic? diag;
        if (isRunsOnLabels)
        {
            var overrides = _hasOverrides ? _jobScopeOverrides : null;
            diag = ExpressionSemanticAnalyzer.CheckRunsOnType(parseResult, expression, location, overrides);
        }
        else if (_hasOverrides)
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

    /// <summary>
    /// Checks that an expression node evaluates to an object type. Used for credentials, services, etc.
    /// </summary>
    private void ValidateExpectedObjectType<TTarget>(
        StringNodeId expressionNode,
        ExpressionValidationContext context,
        string sectionName,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!expressionNode.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var value = Arena.GetStringValue(expressionNode);
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

        var diag = ExpressionSemanticAnalyzer.CheckExpectedObjectType(
            parseResult, body, Arena.GetStringRange(expressionNode), overrides, sectionName);
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

    /// <summary>
    /// Computes a per-expression <see cref="TextRange"/> from the YAML node range and the byte offset
    /// of the <c>$</c> character within the string value. For single-line strings this is a simple
    /// column addition; for multi-line strings newlines in the prefix are counted.
    /// </summary>
    private static TextRange ComputeExpressionLocation(TextRange nodeRange, ReadOnlySpan<byte> value, int dollarOffset)
    {
        var line = nodeRange.StartLine;
        var col = nodeRange.StartColumn;

        for (var i = 0; i < dollarOffset && i < value.Length; i++)
        {
            if (value[i] == (byte)'\n')
            {
                line++;
                col = 1;
            }
            else
            {
                col++;
            }
        }

        return new TextRange(
            nodeRange.Start + dollarOffset,
            0,
            line,
            col,
            line,
            col);
    }

    private void CheckStrategy(Strategy? strategy, Job job)
    {
        if (strategy is null) return;

        CheckNode(strategy.FailFast, ExpressionValidationContext.JobStrategy, static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);
        CheckNode(strategy.MaxParallel, ExpressionValidationContext.JobStrategy, static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);

        if (strategy.Matrix is { } matrix)
        {
            CheckSectionExpression(matrix.Expression, ExpressionValidationContext.JobStrategy, static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);

            if (matrix.Rows is { Count: > 0 })
            {
                foreach (var pair in matrix.Rows)
                {
                    CheckMatrixValues(pair.Value.Values, ExpressionValidationContext.JobStrategy, job);
                    CheckSectionExpression(pair.Value.Expression, ExpressionValidationContext.JobStrategy, static (rule, message, location, j) =>
                        rule.AddJobError(j, message, location), job);
                }
            }
            if (matrix.Include is { Count: > 0 })
            {
                foreach (var combo in matrix.Include)
                {
                    CheckSectionExpression(combo.Expression, ExpressionValidationContext.JobStrategy, static (rule, message, location, j) =>
                        rule.AddJobError(j, message, location), job);
                    CheckMatrixCombinationEntries(combo.Entries, ExpressionValidationContext.JobStrategy, job);
                }
            }
            if (matrix.Exclude is { Count: > 0 })
            {
                foreach (var combo in matrix.Exclude)
                {
                    CheckSectionExpression(combo.Expression, ExpressionValidationContext.JobStrategy, static (rule, message, location, j) =>
                        rule.AddJobError(j, message, location), job);
                    CheckMatrixCombinationEntries(combo.Entries, ExpressionValidationContext.JobStrategy, job);
                }
            }
        }
    }

    private void CheckMatrixValues(IReadOnlyList<RawYamlValue>? values, ExpressionValidationContext context, Job job)
    {
        if (values is null) return;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is RawYamlString str)
            {
                CheckNode(str.Value, context, static (rule, message, location, j) =>
                    rule.AddJobError(j, message, location), job);
            }
        }
    }

    private void CheckMatrixCombinationEntries(IReadOnlyList<SliceMap<RawYamlValue>>? entries, ExpressionValidationContext context, Job job)
    {
        if (entries is null) return;
        for (var i = 0; i < entries.Count; i++)
        {
            foreach (var pair in entries[i])
            {
                if (pair.Value is RawYamlString str)
                {
                    CheckNode(str.Value, context, static (rule, message, location, j) =>
                        rule.AddJobError(j, message, location), job);
                }
            }
        }
    }

    private void CheckContainer(Container? container, ExpressionValidationContext imageCtx, ExpressionValidationContext credentialsCtx, ExpressionValidationContext envCtx, ExpressionValidationContext optionsCtx, Job job)
    {
        if (container is null) return;

        CheckNode(container.Image, imageCtx, static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);
        CheckNode(container.Options, optionsCtx, static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);
        CheckEnv(container.Env, envCtx, static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);

        if (container.Credentials is { } creds)
        {
            CheckNode(creds.Username, credentialsCtx, static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
            CheckNode(creds.Password, credentialsCtx, static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
            CheckSectionExpression(creds.Expression, credentialsCtx, static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
            ValidateExpectedObjectType(creds.Expression, credentialsCtx, "credentials", static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
        }
    }

    private void CheckServices(Services? services, Job job)
    {
        if (services is null) return;

        CheckSectionExpression(services.Expression, ExpressionValidationContext.JobServices, static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);
        ValidateExpectedObjectType(services.Expression, ExpressionValidationContext.JobServices, "services", static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);

        if (services.ServiceMap is not { Count: > 0 }) return;

        foreach (var pair in services.ServiceMap)
        {
            var svc = pair.Value;
            var svcContainer = svc.Container;
            if (svcContainer is null) continue;

            CheckNode(svcContainer.Image, ExpressionValidationContext.JobServices, static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
            CheckNode(svcContainer.Options, ExpressionValidationContext.JobServices, static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
            CheckNode(svcContainer.Entrypoint, ExpressionValidationContext.JobServicesEntrypoint, static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
            CheckNode(svcContainer.Command, ExpressionValidationContext.JobServicesCommand, static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);
            CheckEnv(svcContainer.Env, ExpressionValidationContext.JobServicesEnv, static (rule, message, location, j) =>
                rule.AddJobError(j, message, location), job);

            if (svcContainer.Credentials is { } svcCreds)
            {
                CheckNode(svcCreds.Username, ExpressionValidationContext.JobServicesCredentials, static (rule, message, location, j) =>
                    rule.AddJobError(j, message, location), job);
                CheckNode(svcCreds.Password, ExpressionValidationContext.JobServicesCredentials, static (rule, message, location, j) =>
                    rule.AddJobError(j, message, location), job);
                CheckSectionExpression(svcCreds.Expression, ExpressionValidationContext.JobServicesCredentials, static (rule, message, location, j) =>
                    rule.AddJobError(j, message, location), job);
                ValidateExpectedObjectType(svcCreds.Expression, ExpressionValidationContext.JobServicesCredentials, "credentials", static (rule, message, location, j) =>
                    rule.AddJobError(j, message, location), job);
            }
        }
    }

    private void CheckNode<TTarget>(
        IntNodeId node,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue) return;
        CheckNode(Arena.GetIntExpression(node), context, report, target);
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

    private static string FormatScopeName(ExpressionValidationContext context) => context switch
    {
        ExpressionValidationContext.Concurrency => "workflow concurrency",
        ExpressionValidationContext.DefaultsRunShell => "workflow defaults.run",
        ExpressionValidationContext.Env => "workflow env",
        ExpressionValidationContext.RunName => "workflow run-name",
        ExpressionValidationContext.WorkflowCallInputsDefault => "workflow_call input default",
        ExpressionValidationContext.WorkflowCallOutputsValue => "workflow_call output value",
        ExpressionValidationContext.JobConcurrency => "job concurrency",
        ExpressionValidationContext.JobContainer => "job container",
        ExpressionValidationContext.JobContainerCredentials => "job container credentials",
        ExpressionValidationContext.JobContainerEnv => "job container env",
        ExpressionValidationContext.JobContainerImage => "job container image",
        ExpressionValidationContext.JobContinueOnError => "job continue-on-error",
        ExpressionValidationContext.JobDefaultsRun => "job defaults.run",
        ExpressionValidationContext.JobEnv => "job env",
        ExpressionValidationContext.JobEnvironment => "job environment",
        ExpressionValidationContext.JobEnvironmentUrl => "job environment.url",
        ExpressionValidationContext.JobIf => "job if",
        ExpressionValidationContext.JobName => "job name",
        ExpressionValidationContext.JobOutputs => "job outputs",
        ExpressionValidationContext.JobRunsOn => "job runs-on",
        ExpressionValidationContext.JobSecrets => "job secrets",
        ExpressionValidationContext.JobServices => "job services",
        ExpressionValidationContext.JobServicesCommand => "job services command",
        ExpressionValidationContext.JobServicesCredentials => "job services credentials",
        ExpressionValidationContext.JobServicesEntrypoint => "job services entrypoint",
        ExpressionValidationContext.JobServicesEnv => "job services env",
        ExpressionValidationContext.JobSnapshotIf => "snapshot if",
        ExpressionValidationContext.JobStrategy => "job strategy",
        ExpressionValidationContext.JobTimeoutMinutes => "job timeout-minutes",
        ExpressionValidationContext.JobWith => "job with",
        ExpressionValidationContext.StepContinueOnError => "step continue-on-error",
        ExpressionValidationContext.StepEnv => "step env",
        ExpressionValidationContext.StepId => "step id",
        ExpressionValidationContext.StepIf => "step if",
        ExpressionValidationContext.StepName => "step name",
        ExpressionValidationContext.StepRun => "step run",
        ExpressionValidationContext.StepShell => "step shell",
        ExpressionValidationContext.StepTimeoutMinutes => "step timeout-minutes",
        ExpressionValidationContext.StepWith => "step with",
        ExpressionValidationContext.StepWorkingDirectory => "step working-directory",
        _ => "unknown scope",
    };
}
