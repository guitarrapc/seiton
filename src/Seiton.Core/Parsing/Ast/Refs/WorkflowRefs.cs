using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing.Ast;

/// <summary>The root of a parsed workflow document.</summary>
public readonly struct WorkflowRef
{
    private readonly AstArena? _arena;
    private readonly Workflow? _node;

    internal WorkflowRef(AstArena? arena, Workflow? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    internal Workflow? Node => _node;

    internal AstArena? Arena => _arena;

    public StringRef Name => new(_arena, _node?.Name ?? default);

    public StringRef RunName => new(_arena, _node?.RunName ?? default);

    public EventRefList On => new(_arena, _node?.On);

    public PermissionsRef Permissions => new(_arena, _node?.Permissions);

    public EnvRef Env => new(_arena, _node?.Env);

    public DefaultsRef Defaults => new(_arena, _node?.Defaults);

    public ConcurrencyRef Concurrency => new(_arena, _node?.Concurrency);

    public JobRefMap Jobs => new(_arena, _node?.Jobs);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>A single job in a workflow.</summary>
public readonly struct JobRef : IEquatable<JobRef>, INodeRef<Job, JobRef>
{
    private readonly AstArena? _arena;
    private readonly Job? _node;

    internal JobRef(AstArena? arena, Job? node)
    {
        _arena = arena;
        _node = node;
    }

    static JobRef INodeRef<Job, JobRef>.Create(AstArena? arena, Job node) => new(arena, node);

    public bool HasValue => _node is not null && _arena is not null;

    internal Job? Node => _node;

    public StringRef Id => new(_arena, _node?.Id ?? default);

    public StringRef Name => new(_arena, _node?.Name ?? default);

    public StringRefList Needs => new(_arena, _node?.Needs);

    public RunnerRef RunsOn => new(_arena, _node?.RunsOn);

    public TextRange? RunsOnKeyRange => _node?.RunsOnKeyRange;

    public PermissionsRef Permissions => new(_arena, _node?.Permissions);

    public EnvironmentRef Environment => new(_arena, _node?.Environment);

    public ConcurrencyRef Concurrency => new(_arena, _node?.Concurrency);

    public StringRefMap Outputs => new(_arena, _node?.Outputs);

    public EnvRef Env => new(_arena, _node?.Env);

    public DefaultsRef Defaults => new(_arena, _node?.Defaults);

    public StringRef If => new(_arena, _node?.If ?? default);

    public TextRange? IfKeyRange => _node?.IfKeyRange;

    public StepRefList Steps => new(_arena, _node?.Steps);

    public TextRange? StepsKeyRange => _node?.StepsKeyRange;

    public FloatRef TimeoutMinutes => new(_arena, _node?.TimeoutMinutes ?? default);

    public StrategyRef Strategy => new(_arena, _node?.Strategy);

    public BoolRef ContinueOnError => new(_arena, _node?.ContinueOnError ?? default);

    public ContainerRef Container => new(_arena, _node?.Container);

    public ServicesRef Services => new(_arena, _node?.Services);

    public WorkflowCallRef WorkflowCall => new(_arena, _node?.WorkflowCall);

    public SnapshotRef Snapshot => new(_arena, _node?.Snapshot);

    public TextRange Range => _node?.Range ?? default;

    public bool Equals(JobRef other) => ReferenceEquals(_node, other._node);

    public override bool Equals(object? obj) => obj is JobRef other && Equals(other);

    public override int GetHashCode() => _node is null ? 0 : RuntimeHelpers.GetHashCode(_node);

    public static bool operator ==(JobRef left, JobRef right) => left.Equals(right);

    public static bool operator !=(JobRef left, JobRef right) => !left.Equals(right);
}

/// <summary>A single step within a job (or composite action).</summary>
public readonly struct StepRef : IEquatable<StepRef>
{
    private readonly AstArena? _arena;
    private readonly Step? _node;

    internal StepRef(AstArena? arena, Step? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    internal Step? Node => _node;

    public StringRef Id => new(_arena, _node?.Id ?? default);

    public StringRef If => new(_arena, _node?.If ?? default);

    public TextRange? IfKeyRange => _node?.IfKeyRange;

    public StringRef Name => new(_arena, _node?.Name ?? default);

    /// <summary>Background modifier on <c>run</c> / <c>uses</c> steps only.</summary>
    public BoolRef Background => new(_arena, _node?.Background ?? default);

    public StepExecRef Exec => new(_arena, _node?.Exec);

    public EnvRef Env => new(_arena, _node?.Env);

    public BoolRef ContinueOnError => new(_arena, _node?.ContinueOnError ?? default);

    public FloatRef TimeoutMinutes => new(_arena, _node?.TimeoutMinutes ?? default);

    public TextRange Range => _node?.Range ?? default;

    public bool Equals(StepRef other) => ReferenceEquals(_node, other._node);

    public override bool Equals(object? obj) => obj is StepRef other && Equals(other);

    public override int GetHashCode() => _node is null ? 0 : RuntimeHelpers.GetHashCode(_node);

    public static bool operator ==(StepRef left, StepRef right) => left.Equals(right);

    public static bool operator !=(StepRef left, StepRef right) => !left.Equals(right);
}

/// <summary>The execution payload of a step, discriminated by <see cref="Kind"/>.</summary>
public readonly struct StepExecRef
{
    private readonly AstArena? _arena;
    private readonly StepExec? _node;

    internal StepExecRef(AstArena? arena, StepExec? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    internal StepExec? Node => _node;

    public StepExecKind Kind => _node?.Kind ?? StepExecKind.None;

    public TextRange Range => _node?.Range ?? default;

    /// <summary>The <c>run:</c> payload. Default when <see cref="Kind"/> is not <see cref="StepExecKind.Run"/>.</summary>
    public ExecRunRef AsRun() => new(_arena, _node as ExecRun);

    /// <summary>The <c>uses:</c> payload. Default when <see cref="Kind"/> is not <see cref="StepExecKind.Action"/>.</summary>
    public ExecActionRef AsAction() => new(_arena, _node as ExecAction);

    /// <summary>The <c>wait:</c> payload. Default when <see cref="Kind"/> is not <see cref="StepExecKind.Wait"/>.</summary>
    public ExecWaitRef AsWait() => new(_arena, _node as ExecWait);

    /// <summary>The <c>cancel:</c> payload. Default when <see cref="Kind"/> is not <see cref="StepExecKind.Cancel"/>.</summary>
    public ExecCancelRef AsCancel() => new(_arena, _node as ExecCancel);

    /// <summary>The <c>parallel:</c> payload. Default when <see cref="Kind"/> is not <see cref="StepExecKind.Parallel"/>.</summary>
    public ExecParallelRef AsParallel() => new(_arena, _node as ExecParallel);
}

/// <summary>The payload of a <c>run:</c> step.</summary>
public readonly struct ExecRunRef
{
    private readonly AstArena? _arena;
    private readonly ExecRun? _node;

    internal ExecRunRef(AstArena? arena, ExecRun? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Run => new(_arena, _node?.Run ?? default);

    public StringRef Shell => new(_arena, _node?.Shell ?? default);

    public StringRef WorkingDirectory => new(_arena, _node?.WorkingDirectory ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The payload of a <c>uses:</c> step (action invocation).</summary>
public readonly struct ExecActionRef
{
    private readonly AstArena? _arena;
    private readonly ExecAction? _node;

    internal ExecActionRef(AstArena? arena, ExecAction? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    internal ExecAction? Node => _node;

    public StringRef Uses => new(_arena, _node?.Uses ?? default);

    public TextRange? UsesKeyRange => _node?.UsesKeyRange;

    /// <summary>The <c>with:</c> inputs.</summary>
    public StringRefMap Inputs => new(_arena, _node?.Inputs);

    public StringRef Entrypoint => new(_arena, _node?.Entrypoint ?? default);

    public StringRef Args => new(_arena, _node?.Args ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The payload of a <c>wait:</c> step.</summary>
public readonly struct ExecWaitRef
{
    private readonly AstArena? _arena;
    private readonly ExecWait? _node;

    internal ExecWaitRef(AstArena? arena, ExecWait? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRefList Targets => new(_arena, _node?.Targets);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The payload of a <c>cancel:</c> step.</summary>
public readonly struct ExecCancelRef
{
    private readonly AstArena? _arena;
    private readonly ExecCancel? _node;

    internal ExecCancelRef(AstArena? arena, ExecCancel? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Target => new(_arena, _node?.Target ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The payload of a <c>parallel:</c> step.</summary>
public readonly struct ExecParallelRef
{
    private readonly AstArena? _arena;
    private readonly ExecParallel? _node;

    internal ExecParallelRef(AstArena? arena, ExecParallel? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StepRefList Steps => new(_arena, _node?.Steps);

    public TextRange Range => _node?.Range ?? default;
}
