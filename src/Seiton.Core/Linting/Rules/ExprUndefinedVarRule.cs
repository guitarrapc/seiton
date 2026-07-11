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
    private WorkflowRef _currentWorkflow;
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
    private static readonly (byte[] NameUtf8, ExprType Type)[] _emptyOverrides = [];
    private readonly List<Diagnostic> _propertyDiagnostics = new();
    // Per-job state for incremental step override building.
    // The timeline contains steps in the order their IDs become visible to later steps.
    private readonly List<StepRef> _stepVisibilityTimeline = [];
    private readonly Dictionary<StepRef, int> _stepVisibleBeforeCounts = [];
    // Reusable dictionary for BuildStepsOverrideInto (avoids per-step allocation)
    private readonly Dictionary<Utf8String, ExprType> _stepsOverrideProps = new();
    // Number of timeline entries already materialized into _stepsOverrideProps.
    // Steps are visited in timeline order, so VisitStep only appends the delta
    // (O(steps) per job) instead of rebuilding the whole prefix (O(steps²)).
    private int _stepsOverrideBuiltCount;
    // Reusable dictionaries for BuildMatrixOverrideInto / BuildNeedsOverrideInto (avoids per-job allocation)
    private readonly Dictionary<Utf8String, ExprType> _matrixOverrideProps = new();
    private readonly Dictionary<Utf8String, ExprType> _needsOverrideProps = new();
    // Local action output resolver for building strict step output types
    private LocalActionOutputResolver? _localActionOutputResolver;
    private Func<ReadOnlyMemory<byte>, string[]?>? _localActionOutputResolverFunc;
    // Local reusable workflow output resolver for building strict needs output types
    private LocalReusableWorkflowOutputResolver? _localReusableOutputResolver;
    private Func<ReadOnlyMemory<byte>, string[]?>? _localReusableOutputResolverFunc;

    public override void VisitWorkflowPre(WorkflowRef workflow)
    {
        base.VisitWorkflowPre(workflow);
        _currentWorkflow = workflow;
        var assumeEvents = Config.GetRuleConfig(Id)?.AssumeEvents;
        _inputsOverride = DynamicContextTypeBuilder.BuildInputsOverride(workflow.Node!.On, assumeEvents, Config.Utf8Yaml);
        _secretsOverride = DynamicContextTypeBuilder.BuildSecretsOverride(workflow.Node!.On, Config.Utf8Yaml);

        // Cache github override: only rebuild when source file or event count changes
        if (ReferenceEquals(Config.Utf8Yaml, _cachedGithubYamlRef) && workflow.On.Count == _cachedGithubEventCount)
        {
            _githubOverride = _cachedGithubOverride;
        }
        else
        {
            _githubOverride = DynamicContextTypeBuilder.BuildGithubOverride(workflow.Node!.On, Arena, Config.Utf8Yaml);
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

        if (workflow.Concurrency is { HasValue: true } concurrency)
        {
            CheckNode(concurrency.Group, ExpressionValidationContext.Concurrency, static (rule, message, location, w) =>
                rule.AddWorkflowError(w, message, location), workflow);
            CheckNode(concurrency.Queue, ExpressionValidationContext.Concurrency, static (rule, message, location, w) =>
                rule.AddWorkflowError(w, message, location), workflow);
        }

        if (workflow.Defaults.Run is { HasValue: true } defaultsRun)
        {
            CheckNode(defaultsRun.Shell, ExpressionValidationContext.DefaultsRunShell, static (rule, message, location, w) =>
                rule.AddWorkflowError(w, message, location), workflow);
            CheckNode(defaultsRun.WorkingDirectory, ExpressionValidationContext.DefaultsRunShell, static (rule, message, location, w) =>
                rule.AddWorkflowError(w, message, location), workflow);
        }
    }

    public override void VisitActionMetadataPre(ActionMetadataRef metadata)
    {
        base.VisitActionMetadataPre(metadata);
        _currentWorkflow = default;
        ResetStepOverrideState();
    }

    public override void VisitEvent(EventRef ev)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        if (ev.Kind == EventKind.WorkflowCall)
        {
            var wce = ev.AsWorkflowCall();
            if (wce.Inputs is { HasValue: true } inputs)
            {
                for (var idx = 0; idx < inputs.Count; idx++)
                {
                    var input = inputs[idx];
                    if (!input.Default.HasValue)
                    {
                        continue;
                    }

                    // Build incremental inputs override: only inputs defined before current index
                    var incrementalInputsOverride = DynamicContextTypeBuilder.BuildWorkflowCallInputsOverrideUpTo(wce.Node!.Inputs!, idx);

                    CheckNodeWithOverrides(
                        input.Default,
                        ExpressionValidationContext.WorkflowCallInputsDefault,
                        [incrementalInputsOverride],
                        static (rule, message, location, e) =>
                            rule.AddWorkflowError(rule._currentWorkflow, message, location),
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

    private void ValidateInputDefaultType(WorkflowCallEventInputRef input, (byte[] NameUtf8, ExprType Type)[] overrides)
    {
        var value = input.Default.Value;
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
        var location = input.Default.Range;
        AddWorkflowError(_currentWorkflow, message, location);
    }

    public override void VisitWorkflowPost(WorkflowRef workflow)
    {
        if (Config.Utf8Yaml is null || !_currentWorkflow.HasValue)
        {
            return;
        }

        // Validate workflow_call output value expressions against jobs context
        for (var i = 0; i < workflow.On.Count; i++)
        {
            if (workflow.On[i].Kind != EventKind.WorkflowCall
                || workflow.On[i].AsWorkflowCall().Outputs is not { Count: > 0 } outputs)
            {
                continue;
            }

            var jobsOverride = DynamicContextTypeBuilder.BuildJobsOverride(
                _currentWorkflow.Node!.Jobs, Config.Utf8Yaml);
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

    public override void VisitJobPre(JobRef job)
    {
        if (Config.Utf8Yaml is null)
        {
            _hasOverrides = false;
            return;
        }

        var yaml = Config.Utf8Yaml;
        var matrixOverride = DynamicContextTypeBuilder.BuildMatrixOverrideInto(_matrixOverrideProps, job.Strategy.Matrix, yaml);
        var needsOverride = DynamicContextTypeBuilder.BuildNeedsOverrideInto(
            _needsOverrideProps,
            job.Node!.Needs,
            _currentWorkflow.Node?.Jobs ?? default,
            Arena,
            yaml,
            _localReusableOutputResolverFunc);

        PlanStepVisibility(job.Steps);

        // job scope: matrix, needs, inputs, secrets, github available (steps is NOT available in job scope)
        _jobScopeOverrides[0] = matrixOverride;
        _jobScopeOverrides[1] = needsOverride;
        _jobScopeOverrides[2] = _inputsOverride;
        _jobScopeOverrides[3] = _secretsOverride;
        _jobScopeOverrides[4] = _githubOverride;
        // step scope: initialize with empty steps (extended per-step in VisitStep; the returned
        // ObjectExprType wraps _stepsOverrideProps by reference and observes appended entries)
        _stepScopeOverrides[0] = DynamicContextTypeBuilder.BuildStepsOverrideInto(_stepsOverrideProps, _stepVisibilityTimeline, Arena, yaml, maxStepIndex: 0, _localActionOutputResolverFunc);
        _stepsOverrideBuiltCount = 0;
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
        if (job.RunsOn is { HasValue: true } runsOn)
        {
            CheckNode(runsOn.LabelsExpr, ExpressionValidationContext.JobRunsOn, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job, isRunsOnLabels: true);
            if (runsOn.Labels is { HasValue: true } labels)
            {
                for (var li = 0; li < labels.Count; li++)
                {
                    CheckNode(labels[li], ExpressionValidationContext.JobRunsOn, static (rule, message, location, targetJob) =>
                        rule.AddJobError(targetJob, message, location), job, isRunsOnLabels: true);
                }
            }
            CheckNode(runsOn.Group, ExpressionValidationContext.JobRunsOn, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.concurrency
        if (job.Concurrency is { HasValue: true } jobConcurrency)
        {
            CheckNode(jobConcurrency.Group, ExpressionValidationContext.JobConcurrency, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
            CheckNode(jobConcurrency.Queue, ExpressionValidationContext.JobConcurrency, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.environment
        if (job.Environment is { HasValue: true } environment)
        {
            CheckNode(environment.Name, ExpressionValidationContext.JobEnvironment, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
            CheckNode(environment.Url, ExpressionValidationContext.JobEnvironmentUrl, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.defaults.run
        if (job.Defaults.Run is { HasValue: true } jobDefaultsRun)
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
        if (job.Snapshot is { HasValue: true } snapshot)
        {
            CheckNode(snapshot.If, ExpressionValidationContext.JobSnapshotIf, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }

        // job.secrets (reusable workflow call)
        var callSecrets = job.WorkflowCall.Secrets;
        if (callSecrets is { Count: > 0 })
        {
            foreach (var pair in callSecrets)
            {
                CheckNode(pair.Value.Value, ExpressionValidationContext.JobSecrets, static (rule, message, location, targetJob) =>
                    rule.AddJobError(targetJob, message, location), job);
            }
        }

        var callInputs = job.WorkflowCall.Inputs;
        if (!callInputs.HasValue || callInputs.Count == 0)
        {
            return;
        }

        foreach (var pair in callInputs)
        {
            var input = pair.Value;
            CheckNode(input.Value, ExpressionValidationContext.JobWith, static (rule, message, location, targetJob) =>
                rule.AddJobError(targetJob, message, location), job);
        }
    }

    public override void VisitStep(StepRef step)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        // Extend the steps override to include only steps visible before the current one.
        // Visitor order matches timeline order, so appending the delta suffices.
        if (_hasOverrides && _stepVisibleBeforeCounts.TryGetValue(step, out var visibleBeforeCount))
        {
            if (visibleBeforeCount > _stepsOverrideBuiltCount)
            {
                DynamicContextTypeBuilder.AppendStepsOverrideInto(
                    _stepsOverrideProps, _stepVisibilityTimeline, Arena, Config.Utf8Yaml, _stepsOverrideBuiltCount, visibleBeforeCount, _localActionOutputResolverFunc);
                _stepsOverrideBuiltCount = visibleBeforeCount;
            }
            else if (visibleBeforeCount < _stepsOverrideBuiltCount)
            {
                // Defensive: only reachable if visit order ever diverges from timeline order.
                _stepScopeOverrides[0] = DynamicContextTypeBuilder.BuildStepsOverrideInto(
                    _stepsOverrideProps, _stepVisibilityTimeline, Arena, Config.Utf8Yaml, maxStepIndex: visibleBeforeCount, _localActionOutputResolverFunc);
                _stepsOverrideBuiltCount = visibleBeforeCount;
            }
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

        if (step.Exec.Kind == StepExecKind.Run)
        {
            var run = step.Exec.AsRun();
            CheckNode(run.Run, ExpressionValidationContext.StepRun, static (rule, message, location, targetStep) =>
                rule.AddStepError(targetStep, message, location), step);
            CheckNode(run.Shell, ExpressionValidationContext.StepShell, static (rule, message, location, targetStep) =>
                rule.AddStepError(targetStep, message, location), step);
            CheckNode(run.WorkingDirectory, ExpressionValidationContext.StepWorkingDirectory, static (rule, message, location, targetStep) =>
                rule.AddStepError(targetStep, message, location), step);
        }
        else if (step.Exec.Kind == StepExecKind.Action && step.Exec.AsAction().Inputs is { Count: > 0 } actionInputs)
        {
            foreach (var pair in actionInputs)
            {
                CheckNode(pair.Value, ExpressionValidationContext.StepWith, static (rule, message, location, targetStep) =>
                    rule.AddStepError(targetStep, message, location), step);
            }
        }
    }

    private void CheckEnv<TTarget>(
        EnvRef env,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!env.HasValue)
        {
            return;
        }

        CheckSectionExpression(env.Expression, context, report, target);

        // When env is a single expression (${{ expr }}), check that it resolves to object type
        ValidateEnvMappingType(env.Expression, context, report, target);

        var vars = env.Vars;
        if (!vars.HasValue || vars.Count == 0)
        {
            return;
        }

        foreach (var pair in vars)
        {
            var envVar = pair.Value;
            CheckNode(envVar.Name, context, report, target);
            CheckNode(envVar.Value, context, report, target);
        }
    }

    private void PlanStepVisibility(StepRefList steps)
    {
        _stepVisibilityTimeline.Clear();
        _stepVisibleBeforeCounts.Clear();

        if (!steps.HasValue || steps.Count == 0)
        {
            return;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            PlanStepVisibilityCore(steps[i]);
        }
    }

    private void ResetStepOverrideState()
    {
        _hasOverrides = false;
        _stepVisibilityTimeline.Clear();
        _stepVisibleBeforeCounts.Clear();
        _stepsOverrideProps.Clear();
        _stepsOverrideBuiltCount = 0;
    }

    private void PlanStepVisibilityCore(StepRef step)
    {
        _stepVisibleBeforeCounts[step] = _stepVisibilityTimeline.Count;

        if (step.Exec.Kind == StepExecKind.Parallel && step.Exec.AsParallel().Steps is { Count: > 0 } children)
        {
            for (var i = 0; i < children.Count; i++)
            {
                PlanParallelChildVisibility(children[i]);
            }

            AddExportedSteps(step);
            return;
        }

        _stepVisibilityTimeline.Add(step);
    }

    private void PlanParallelChildVisibility(StepRef step)
    {
        _stepVisibleBeforeCounts[step] = _stepVisibilityTimeline.Count;

        if (step.Exec.Kind == StepExecKind.Parallel && step.Exec.AsParallel().Steps is { Count: > 0 } children)
        {
            for (var i = 0; i < children.Count; i++)
            {
                PlanParallelChildVisibility(children[i]);
            }
        }
    }

    private void AddExportedSteps(StepRef step)
    {
        _stepVisibilityTimeline.Add(step);

        if (step.Exec.Kind != StepExecKind.Parallel || step.Exec.AsParallel().Steps is not { Count: > 0 } children)
        {
            return;
        }

        for (var i = 0; i < children.Count; i++)
        {
            AddExportedSteps(children[i]);
        }
    }

    private void CheckNode<TTarget>(
        FloatRef node,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue) return;
        CheckNode(node.Expression, context, report, target);
    }

    private void CheckNode<TTarget>(
        BoolRef node,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue) return;
        CheckNode(node.Expression, context, report, target);
    }

    private void CheckNode<TTarget>(
        StringRef node,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target,
        bool isRunsOnLabels = false)
    {
        if (!node.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var value = node.Value;
        if (value.Length == 0)
        {
            return;
        }

        var hasEmbeddedExpression = ExpressionScanHelpers.ContainsExpressionMarker(value);
        var parseWholeValue = context is ExpressionValidationContext.JobIf or ExpressionValidationContext.StepIf or ExpressionValidationContext.JobSnapshotIf;

        if (parseWholeValue && !hasEmbeddedExpression)
        {
            ValidateExpression(value, context, node.Range, report, target);
            return;
        }

        var nodeRange = node.Range;
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
        StringRef node,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue || Config.Utf8Yaml is null) return;
        var value = node.Value;
        if (value.Length == 0) return;

        var nodeRange = node.Range;
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

        // Validate property access against context types (with dynamic overrides when available)
        var overrides = _hasOverrides
            ? (Availability.IsStepLevel(context) ? _stepScopeOverrides : _jobScopeOverrides)
            : _emptyOverrides;

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
        StringRef node,
        ExpressionValidationContext context,
        (byte[] NameUtf8, ExprType Type)[] overrides,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var value = node.Value;
        if (value.Length == 0)
        {
            return;
        }

        var nodeRange = node.Range;
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
            var model = Config.SemanticModel;
            if (!model.IsContextAvailable(context, rootName))
            {
                var rootNameText = Encoding.UTF8.GetString(rootName);
                var scopeText = FormatScopeName(context);
                if (model.IsBuiltinContext(rootName))
                {
                    var availableText = model.FormatAvailableContexts(context);
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
                var model = Config.SemanticModel;

                // Status check functions: only in if conditions
                if (!model.IsIfContext(context) && model.IsStatusCheckFunction(funcName))
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
                if (model.IsHashFilesFunction(funcName) && !model.IsStepLevel(context))
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
        StringRef envExpression,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!envExpression.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var value = envExpression.Value;
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
            parseResult, body, envExpression.Range, overrides);
        if (diag is { } d)
        {
            report(this, d.Message, d.Location, target);
        }
    }

    /// <summary>
    /// Checks that an expression node evaluates to an object type. Used for credentials, services, etc.
    /// </summary>
    private void ValidateExpectedObjectType<TTarget>(
        StringRef expressionNode,
        ExpressionValidationContext context,
        string sectionName,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!expressionNode.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var value = expressionNode.Value;
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
            parseResult, body, expressionNode.Range, overrides, sectionName);
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

    private void CheckStrategy(StrategyRef strategy, JobRef job)
    {
        if (!strategy.HasValue) return;

        CheckNode(strategy.FailFast, ExpressionValidationContext.JobStrategy, static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);
        CheckNode(strategy.MaxParallel, ExpressionValidationContext.JobStrategy, static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);

        if (strategy.Matrix is { HasValue: true } matrix)
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

    private void CheckMatrixValues(RawYamlRefList values, ExpressionValidationContext context, JobRef job)
    {
        if (!values.HasValue) return;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Kind == RawYamlKind.String)
            {
                CheckNode(values[i].Scalar, context, static (rule, message, location, j) =>
                    rule.AddJobError(j, message, location), job);
            }
        }
    }

    private void CheckMatrixCombinationEntries(CombinationEntryRefList entries, ExpressionValidationContext context, JobRef job)
    {
        if (!entries.HasValue) return;
        for (var i = 0; i < entries.Count; i++)
        {
            foreach (var pair in entries[i])
            {
                if (pair.Value.Kind == RawYamlKind.String)
                {
                    CheckNode(pair.Value.Scalar, context, static (rule, message, location, j) =>
                        rule.AddJobError(j, message, location), job);
                }
            }
        }
    }

    private void CheckContainer(ContainerRef container, ExpressionValidationContext imageCtx, ExpressionValidationContext credentialsCtx, ExpressionValidationContext envCtx, ExpressionValidationContext optionsCtx, JobRef job)
    {
        if (!container.HasValue) return;

        CheckNode(container.Image, imageCtx, static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);
        CheckNode(container.Options, optionsCtx, static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);
        CheckEnv(container.Env, envCtx, static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);

        if (container.Credentials is { HasValue: true } creds)
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

    private void CheckServices(ServicesRef services, JobRef job)
    {
        if (!services.HasValue) return;

        CheckSectionExpression(services.Expression, ExpressionValidationContext.JobServices, static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);
        ValidateExpectedObjectType(services.Expression, ExpressionValidationContext.JobServices, "services", static (rule, message, location, j) =>
            rule.AddJobError(j, message, location), job);

        if (services.ServiceMap is not { Count: > 0 }) return;

        foreach (var pair in services.ServiceMap)
        {
            var svc = pair.Value;
            var svcContainer = svc.Container;
            if (!svcContainer.HasValue) continue;

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

            if (svcContainer.Credentials is { HasValue: true } svcCreds)
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
        IntRef node,
        ExpressionValidationContext context,
        Action<ExprUndefinedVarRule, string, TextRange, TTarget> report,
        TTarget target)
    {
        if (!node.HasValue) return;
        CheckNode(node.Expression, context, report, target);
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
