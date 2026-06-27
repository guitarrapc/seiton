using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

internal static class BackgroundStepFlowAnalyzer
{
    private const int MaxConcurrentBackgrounds = 10;

    internal readonly struct RegisteredStep
    {
        public readonly bool IsBackground;

        public RegisteredStep(bool isBackground) => IsBackground = isBackground;
    }

    internal sealed class State
    {
        internal Dictionary<Utf8String, RegisteredStep> Registry { get; } = new();
        internal HashSet<Utf8String> ActiveIds { get; } = new();
        internal int ActiveCount { get; set; }
        internal List<Finding> Findings { get; } = [];
    }

    internal readonly struct Finding
    {
        public Finding(Step step, TextRange location, DiagnosticSeverity severity, string message, string structurePath)
        {
            Step = step;
            Location = location;
            Severity = severity;
            Message = message;
            StructurePath = structurePath;
        }

        public Step Step { get; }
        public TextRange Location { get; }
        public DiagnosticSeverity Severity { get; }
        public string Message { get; }
        public string StructurePath { get; }
    }

    internal static void Analyze(
        Job job,
        AstArena arena,
        LintConfig config,
        string jobStructurePrefix,
        State state)
    {
        state.Findings.Clear();
        state.Registry.Clear();
        state.ActiveIds.Clear();
        state.ActiveCount = 0;

        var steps = job.Steps;
        if (steps is null or { Count: 0 } || config.Utf8Yaml is null)
        {
            return;
        }

        if (!NeedsAnalysis(steps, arena))
        {
            return;
        }

        var peakWarningEmitted = false;
        for (var i = 0; i < steps.Count; i++)
        {
            ProcessTopLevelStep(
                steps[i],
                i,
                steps,
                arena,
                config,
                jobStructurePrefix,
                state,
                ref peakWarningEmitted);
        }

        state.ActiveIds.Clear();
        state.ActiveCount = 0;
    }

    private static bool NeedsAnalysis(IReadOnlyList<Step> steps, AstArena arena)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (StepMightAffectFlow(steps[i], arena))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StepMightAffectFlow(Step step, AstArena arena)
    {
        if (IsExplicitBackground(step, arena))
        {
            return true;
        }

        return step.Exec.Kind switch
        {
            StepExecKind.Wait or StepExecKind.WaitAll or StepExecKind.Cancel or StepExecKind.Parallel => true,
            _ => false,
        };
    }

    private static void ProcessTopLevelStep(
        Step step,
        int stepIndex,
        IReadOnlyList<Step> topLevelSteps,
        AstArena arena,
        LintConfig config,
        string jobStructurePrefix,
        State state,
        ref bool peakWarningEmitted)
    {
        var stepPath = $"{jobStructurePrefix}.steps[{stepIndex}]";
        var registry = state.Registry;
        var findings = state.Findings;

        switch (step.Exec)
        {
            case ExecParallel parallel:
                TryRegisterStaticId(step, arena, registry, isBackground: false);
                RegisterParallelChildren(parallel.Steps, arena, registry);
                AddParallelChildrenToActive(parallel.Steps, arena, config, state);
                MaybeEmitPeakWarning(step, parallel.Range, $"{stepPath}.parallel", findings, state, ref peakWarningEmitted);
                RemoveParallelChildrenFromActive(parallel.Steps, arena, config, state);
                break;

            case ExecWait wait:
                TryRegisterStaticId(step, arena, registry, isBackground: false);
                ValidateWaitTargets(step, wait, stepPath, $"{stepPath}.wait", topLevelSteps, stepIndex, arena, registry, findings);
                RemoveValidTargets(wait.Targets, topLevelSteps, stepIndex, arena, registry, state);
                break;

            case ExecCancel cancel:
                TryRegisterStaticId(step, arena, registry, isBackground: false);
                ValidateCancelTarget(step, cancel, stepPath, $"{stepPath}.cancel", topLevelSteps, stepIndex, arena, registry, findings);
                RemoveValidCancelTarget(cancel, topLevelSteps, stepIndex, arena, registry, state);
                break;

            case ExecWaitAll:
                TryRegisterStaticId(step, arena, registry, isBackground: false);
                state.ActiveIds.Clear();
                state.ActiveCount = 0;
                break;

            default:
                if (IsExplicitBackground(step, arena))
                {
                    TryRegisterStaticId(step, arena, registry, isBackground: true);
                    if (ShouldCountForPeak(step, arena, config))
                    {
                        AddStepToActive(step, arena, state);
                        MaybeEmitPeakWarning(step, step.Range, stepPath, findings, state, ref peakWarningEmitted);
                    }
                }
                else
                {
                    TryRegisterStaticId(step, arena, registry, isBackground: false);
                }

                break;
        }
    }

    private static void RegisterParallelChildren(IReadOnlyList<Step>? children, AstArena arena, Dictionary<Utf8String, RegisteredStep> registry)
    {
        if (children is null)
        {
            return;
        }

        for (var i = 0; i < children.Count; i++)
        {
            TryRegisterStaticId(children[i], arena, registry, isBackground: true);
        }
    }

    private static void AddParallelChildrenToActive(IReadOnlyList<Step>? children, AstArena arena, LintConfig config, State state)
    {
        if (children is null)
        {
            return;
        }

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (!ShouldCountForPeak(child, arena, config))
            {
                continue;
            }

            state.ActiveCount++;
            if (TryGetStaticIdKey(child, arena, out var key))
            {
                state.ActiveIds.Add(key);
            }
        }
    }

    private static void RemoveParallelChildrenFromActive(IReadOnlyList<Step>? children, AstArena arena, LintConfig config, State state)
    {
        if (children is null)
        {
            return;
        }

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (!ShouldCountForPeak(child, arena, config))
            {
                continue;
            }

            state.ActiveCount = Math.Max(0, state.ActiveCount - 1);
            if (TryGetStaticIdKey(child, arena, out var key))
            {
                state.ActiveIds.Remove(key);
            }
        }
    }

    private static void ValidateWaitTargets(
        Step step,
        ExecWait wait,
        string stepPath,
        string structurePath,
        IReadOnlyList<Step> topLevelSteps,
        int stepIndex,
        AstArena arena,
        Dictionary<Utf8String, RegisteredStep> registry,
        List<Finding> findings)
    {
        var targets = wait.Targets;
        if (targets is null)
        {
            return;
        }

        for (var i = 0; i < targets.Count; i++)
        {
            ValidateReference(
                step,
                targets[i],
                "wait",
                structurePath,
                topLevelSteps,
                stepIndex,
                arena,
                registry,
                findings);
        }
    }

    private static void ValidateCancelTarget(
        Step step,
        ExecCancel cancel,
        string stepPath,
        string structurePath,
        IReadOnlyList<Step> topLevelSteps,
        int stepIndex,
        AstArena arena,
        Dictionary<Utf8String, RegisteredStep> registry,
        List<Finding> findings)
    {
        if (!cancel.Target.HasValue)
        {
            return;
        }

        ValidateReference(
            step,
            cancel.Target,
            "cancel",
            structurePath,
            topLevelSteps,
            stepIndex,
            arena,
            registry,
            findings);
    }

    private static void ValidateReference(
        Step step,
        StringNodeId targetId,
        string refKind,
        string structurePath,
        IReadOnlyList<Step> topLevelSteps,
        int stepIndex,
        AstArena arena,
        Dictionary<Utf8String, RegisteredStep> registry,
        List<Finding> findings)
    {
        var targetSpan = arena.GetStringValue(targetId);
        if (targetSpan.Length == 0)
        {
            return;
        }

        var targetKey = Utf8String.FromLowerAscii(targetSpan);
        var location = arena.GetStringRange(targetId);

        if (registry.TryGetValue(targetKey, out var registered))
        {
            if (!registered.IsBackground)
            {
                findings.Add(new Finding(
                    step,
                    location,
                    DiagnosticSeverity.Error,
                    $"\"{refKind}\" references step id '{DecodeId(targetSpan)}' that is not a background step",
                    structurePath));
            }

            return;
        }

        if (TryForwardScanFindStaticId(topLevelSteps, stepIndex + 1, targetSpan, arena, out var forwardIsBackground))
        {
            var idText = DecodeId(targetSpan);
            if (forwardIsBackground)
            {
                findings.Add(new Finding(
                    step,
                    location,
                    DiagnosticSeverity.Error,
                    $"background step id '{idText}' is referenced by \"{refKind}\" before it is defined",
                    structurePath));
            }
            else
            {
                findings.Add(new Finding(
                    step,
                    location,
                    DiagnosticSeverity.Error,
                    $"\"{refKind}\" references step id '{idText}' that is not a background step",
                    structurePath));
            }

            return;
        }

        findings.Add(new Finding(
            step,
            location,
            DiagnosticSeverity.Error,
            $"\"{refKind}\" references unknown background step id '{DecodeId(targetSpan)}'",
            structurePath));
    }

    private static string DecodeId(ReadOnlySpan<byte> targetSpan) => Encoding.UTF8.GetString(targetSpan);

    private static void RemoveValidTargets(
        IReadOnlyList<StringNodeId>? targets,
        IReadOnlyList<Step> topLevelSteps,
        int stepIndex,
        AstArena arena,
        Dictionary<Utf8String, RegisteredStep> registry,
        State state)
    {
        if (targets is null)
        {
            return;
        }

        for (var i = 0; i < targets.Count; i++)
        {
            if (TryResolveValidBackgroundTarget(targets[i], topLevelSteps, stepIndex, arena, registry, out var key))
            {
                if (state.ActiveIds.Remove(key))
                {
                    state.ActiveCount = Math.Max(0, state.ActiveCount - 1);
                }
            }
        }
    }

    private static void RemoveValidCancelTarget(
        ExecCancel cancel,
        IReadOnlyList<Step> topLevelSteps,
        int stepIndex,
        AstArena arena,
        Dictionary<Utf8String, RegisteredStep> registry,
        State state)
    {
        if (!cancel.Target.HasValue)
        {
            return;
        }

        if (TryResolveValidBackgroundTarget(cancel.Target, topLevelSteps, stepIndex, arena, registry, out var key)
            && state.ActiveIds.Remove(key))
        {
            state.ActiveCount = Math.Max(0, state.ActiveCount - 1);
        }
    }

    private static bool TryResolveValidBackgroundTarget(
        StringNodeId targetId,
        IReadOnlyList<Step> topLevelSteps,
        int stepIndex,
        AstArena arena,
        Dictionary<Utf8String, RegisteredStep> registry,
        out Utf8String key)
    {
        key = default;
        var targetSpan = arena.GetStringValue(targetId);
        if (targetSpan.Length == 0)
        {
            return false;
        }

        key = Utf8String.FromLowerAscii(targetSpan);
        if (registry.TryGetValue(key, out var registered))
        {
            return registered.IsBackground;
        }

        return false;
    }

    private static bool TryForwardScanFindStaticId(
        IReadOnlyList<Step> steps,
        int startIndex,
        ReadOnlySpan<byte> targetSpan,
        AstArena arena,
        out bool isBackground)
    {
        for (var i = startIndex; i < steps.Count; i++)
        {
            if (TryFindStaticIdInStep(steps[i], targetSpan, arena, out isBackground))
            {
                return true;
            }
        }

        isBackground = false;
        return false;
    }

    private static bool TryFindStaticIdInStep(Step step, ReadOnlySpan<byte> targetSpan, AstArena arena, out bool isBackground)
    {
        if (StepHasMatchingStaticId(step, targetSpan, arena))
        {
            isBackground = IsExplicitBackground(step, arena);
            return true;
        }

        if (step.Exec is ExecParallel { Steps: { } children })
        {
            for (var i = 0; i < children.Count; i++)
            {
                if (StepHasMatchingStaticId(children[i], targetSpan, arena))
                {
                    isBackground = true;
                    return true;
                }
            }
        }

        isBackground = false;
        return false;
    }

    private static bool StepHasMatchingStaticId(Step step, ReadOnlySpan<byte> targetSpan, AstArena arena)
    {
        if (!TryGetStaticIdKey(step, arena, out var key))
        {
            return false;
        }

        return EqualsAsciiIgnoreCase(key.Span, targetSpan);
    }

    private static void TryRegisterStaticId(Step step, AstArena arena, Dictionary<Utf8String, RegisteredStep> registry, bool isBackground)
    {
        if (!TryGetStaticIdKey(step, arena, out var key))
        {
            return;
        }

        registry.TryAdd(key, new RegisteredStep(isBackground));
    }

    private static void AddStepToActive(Step step, AstArena arena, State state)
    {
        state.ActiveCount++;
        if (TryGetStaticIdKey(step, arena, out var key))
        {
            state.ActiveIds.Add(key);
        }
    }

    private static bool TryGetStaticIdKey(Step step, AstArena arena, out Utf8String key)
    {
        key = default;
        if (!step.Id.HasValue)
        {
            return false;
        }

        if (ExpressionScanHelpers.ContainsExpressionMarker(step.Id, arena))
        {
            return false;
        }

        var idSpan = arena.GetStringValue(step.Id);
        if (idSpan.Length == 0)
        {
            return false;
        }

        key = Utf8String.FromLowerAscii(idSpan);
        return true;
    }

    private static bool IsExplicitBackground(Step step, AstArena arena)
    {
        return step.Background.HasValue
            && arena.GetBoolValue(step.Background)
            && step.Exec.Kind is StepExecKind.Run or StepExecKind.Action;
    }

    private static bool ShouldCountForPeak(Step step, AstArena arena, LintConfig config)
    {
        if (!step.If.HasValue)
        {
            return true;
        }

        var raw = arena.GetStringValue(step.If);
        if (raw.Length == 0)
        {
            return false;
        }

        if (!ExpressionScanHelpers.ContainsExpressionMarker(raw))
        {
            return IsScalarTruthy(raw);
        }

        var expression = ExpressionScanHelpers.TryExtractExpressionBody(raw, out var body) ? body : raw;
        var parseResult = config.ParseExpression(expression);
        if (!parseResult.HasRoot || parseResult.Diagnostics.Length > 0)
        {
            return false;
        }

        return ExpressionConstantEvaluator.TryEvaluateConstantBool(
            parseResult.RootNode,
            parseResult.Nodes,
            parseResult.Arguments,
            expression,
            out var value)
            && value;
    }

    private static bool IsScalarTruthy(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (value.SequenceEqual("false"u8)
            || value.SequenceEqual("False"u8)
            || value.SequenceEqual("FALSE"u8)
            || value.SequenceEqual("null"u8)
            || value.SequenceEqual("Null"u8)
            || value.SequenceEqual("NULL"u8)
            || value.SequenceEqual("0"u8))
        {
            return false;
        }

        return true;
    }

    private static void MaybeEmitPeakWarning(
        Step step,
        TextRange location,
        string structurePath,
        List<Finding> findings,
        State state,
        ref bool peakWarningEmitted)
    {
        if (peakWarningEmitted || state.ActiveCount <= MaxConcurrentBackgrounds)
        {
            return;
        }

        peakWarningEmitted = true;
        findings.Add(new Finding(
            step,
            location,
            DiagnosticSeverity.Warning,
            "more than 10 background steps may run concurrently in this job",
            structurePath));
    }

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a == b)
            {
                continue;
            }

            if (a is >= (byte)'A' and <= (byte)'Z')
            {
                a = (byte)(a + 32);
            }

            if (b is >= (byte)'A' and <= (byte)'Z')
            {
                b = (byte)(b + 32);
            }

            if (a != b)
            {
                return false;
            }
        }

        return true;
    }
}
