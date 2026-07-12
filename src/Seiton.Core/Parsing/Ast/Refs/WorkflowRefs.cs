namespace Seiton.Core.Parsing.Ast;

/// <summary>The root of a parsed workflow document.</summary>
public readonly struct WorkflowRef
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly Workflow? _node;

    internal WorkflowRef(AstArena? arena, Workflow? node)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    internal Workflow? Node => _node;

    internal AstArena? Arena => _arena;

    public StringRef Name => new(ArenaChecked, _node?.Name ?? default);

    public StringRef RunName => new(ArenaChecked, _node?.RunName ?? default);

    public EventRefList On => new(ArenaChecked, _node?.On ?? default);

    public PermissionsRef Permissions => new(ArenaChecked, _node?.Permissions ?? default);

    public EnvRef Env => new(ArenaChecked, _node?.Env ?? default);

    public DefaultsRef Defaults => new(ArenaChecked, _node?.Defaults ?? default);

    public ConcurrencyRef Concurrency => new(ArenaChecked, _node?.Concurrency ?? default);

    public JobRefMap Jobs => new(ArenaChecked, _node?.Jobs ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>A single job in a workflow.</summary>
public readonly struct JobRef : IEquatable<JobRef>
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly JobId _id;

    internal JobRef(AstArena? arena, JobId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Id => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Id) : default;

    public StringRef Name => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Name) : default;

    public StringRefList Needs => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Needs) : default;

    /// <summary>The raw <c>needs:</c> handle range (for callers that resolve via the arena).</summary>
    internal StringIdRange NeedsRange => HasValue ? ArenaChecked!.GetJob(_id).Needs : default;

    public RunnerRef RunsOn => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).RunsOn) : default;

    public TextRange? RunsOnKeyRange => HasValue ? ArenaChecked!.GetJob(_id).RunsOnKeyRange : null;

    public PermissionsRef Permissions => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Permissions) : default;

    public EnvironmentRef Environment => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Environment) : default;

    public ConcurrencyRef Concurrency => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Concurrency) : default;

    public StringRefMap Outputs => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Outputs) : default;

    public EnvRef Env => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Env) : default;

    public DefaultsRef Defaults => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Defaults) : default;

    public StringRef If => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).If) : default;

    public TextRange? IfKeyRange => HasValue ? ArenaChecked!.GetJob(_id).IfKeyRange : null;

    public StepRefList Steps => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Steps) : default;

    public TextRange? StepsKeyRange => HasValue ? ArenaChecked!.GetJob(_id).StepsKeyRange : null;

    public FloatRef TimeoutMinutes => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).TimeoutMinutes) : default;

    public StrategyRef Strategy => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Strategy) : default;

    public BoolRef ContinueOnError => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).ContinueOnError) : default;

    public ContainerRef Container => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Container) : default;

    public ServicesRef Services => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Services) : default;

    public WorkflowCallRef WorkflowCall => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).WorkflowCall) : default;

    public SnapshotRef Snapshot => HasValue ? new(ArenaChecked, ArenaChecked!.GetJob(_id).Snapshot) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetJob(_id).Range : default;

    public bool Equals(JobRef other) => ReferenceEquals(_arena, other._arena) && _id.Equals(other._id);

    public override bool Equals(object? obj) => obj is JobRef other && Equals(other);

    public override int GetHashCode() => _id.GetHashCode();

    public static bool operator ==(JobRef left, JobRef right) => left.Equals(right);

    public static bool operator !=(JobRef left, JobRef right) => !left.Equals(right);
}

/// <summary>A single step within a job (or composite action).</summary>
public readonly struct StepRef : IEquatable<StepRef>
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly StepId _id;

    internal StepRef(AstArena? arena, StepId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Id => HasValue ? new(ArenaChecked, ArenaChecked!.GetStep(_id).Id) : default;

    public StringRef If => HasValue ? new(ArenaChecked, ArenaChecked!.GetStep(_id).If) : default;

    public TextRange? IfKeyRange => HasValue ? ArenaChecked!.GetStep(_id).IfKeyRange : null;

    public StringRef Name => HasValue ? new(ArenaChecked, ArenaChecked!.GetStep(_id).Name) : default;

    /// <summary>Background modifier on <c>run</c> / <c>uses</c> steps only.</summary>
    public BoolRef Background => HasValue ? new(ArenaChecked, ArenaChecked!.GetStep(_id).Background) : default;

    public StepExecRef Exec
    {
        get
        {
            if (!HasValue)
            {
                return default;
            }

            ref readonly var row = ref ArenaChecked!.GetStep(_id);
            return new StepExecRef(_arena, row.ExecKind, row.ExecPayload);
        }
    }

    public EnvRef Env => HasValue ? new(ArenaChecked, ArenaChecked!.GetStep(_id).Env) : default;

    public BoolRef ContinueOnError => HasValue ? new(ArenaChecked, ArenaChecked!.GetStep(_id).ContinueOnError) : default;

    public FloatRef TimeoutMinutes => HasValue ? new(ArenaChecked, ArenaChecked!.GetStep(_id).TimeoutMinutes) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetStep(_id).Range : default;

    public bool Equals(StepRef other) => ReferenceEquals(_arena, other._arena) && _id.Equals(other._id);

    public override bool Equals(object? obj) => obj is StepRef other && Equals(other);

    public override int GetHashCode() => _id.GetHashCode();

    public static bool operator ==(StepRef left, StepRef right) => left.Equals(right);

    public static bool operator !=(StepRef left, StepRef right) => !left.Equals(right);
}

/// <summary>The execution payload of a step, discriminated by <see cref="Kind"/>.</summary>
public readonly struct StepExecRef
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly StepExecKind _kind;
    // 1-based index into the payload table selected by _kind (0 = none).
    private readonly int _payload;

    internal StepExecRef(AstArena? arena, StepExecKind kind, int payload)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _kind = kind;
        _payload = payload;
    }

    public bool HasValue => _arena is not null && _payload > 0;

    public StepExecKind Kind => _kind;

    public TextRange Range
    {
        get
        {
            if (_arena is null || _payload == 0)
            {
                return default;
            }

            return _kind switch
            {
                StepExecKind.Run => ArenaChecked!.GetExecRun(_payload).Range,
                StepExecKind.Action => ArenaChecked!.GetExecAction(_payload).Range,
                StepExecKind.Wait => ArenaChecked!.GetExecWait(_payload).Range,
                StepExecKind.WaitAll => ArenaChecked!.GetExecWaitAll(_payload).Range,
                StepExecKind.Cancel => ArenaChecked!.GetExecCancel(_payload).Range,
                StepExecKind.Parallel => ArenaChecked!.GetExecParallel(_payload).Range,
                _ => default,
            };
        }
    }

    /// <summary>The <c>run:</c> payload. Default when <see cref="Kind"/> is not <see cref="StepExecKind.Run"/>.</summary>
    public ExecRunRef AsRun() => _kind == StepExecKind.Run && _payload > 0 ? new(ArenaChecked, _payload) : default;

    /// <summary>The <c>uses:</c> payload. Default when <see cref="Kind"/> is not <see cref="StepExecKind.Action"/>.</summary>
    public ExecActionRef AsAction() => _kind == StepExecKind.Action && _payload > 0 ? new(ArenaChecked, _payload) : default;

    /// <summary>The <c>wait:</c> payload. Default when <see cref="Kind"/> is not <see cref="StepExecKind.Wait"/>.</summary>
    public ExecWaitRef AsWait() => _kind == StepExecKind.Wait && _payload > 0 ? new(ArenaChecked, _payload) : default;

    /// <summary>The <c>cancel:</c> payload. Default when <see cref="Kind"/> is not <see cref="StepExecKind.Cancel"/>.</summary>
    public ExecCancelRef AsCancel() => _kind == StepExecKind.Cancel && _payload > 0 ? new(ArenaChecked, _payload) : default;

    /// <summary>The <c>parallel:</c> payload. Default when <see cref="Kind"/> is not <see cref="StepExecKind.Parallel"/>.</summary>
    public ExecParallelRef AsParallel() => _kind == StepExecKind.Parallel && _payload > 0 ? new(ArenaChecked, _payload) : default;
}

/// <summary>The payload of a <c>run:</c> step.</summary>
public readonly struct ExecRunRef
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly int _payload;

    internal ExecRunRef(AstArena? arena, int payload)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _payload = payload;
    }

    public bool HasValue => _arena is not null && _payload > 0;

    public StringRef Run => HasValue ? new(ArenaChecked, ArenaChecked!.GetExecRun(_payload).Run) : default;

    public StringRef Shell => HasValue ? new(ArenaChecked, ArenaChecked!.GetExecRun(_payload).Shell) : default;

    public StringRef WorkingDirectory => HasValue ? new(ArenaChecked, ArenaChecked!.GetExecRun(_payload).WorkingDirectory) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetExecRun(_payload).Range : default;
}

/// <summary>The payload of a <c>uses:</c> step (action invocation).</summary>
public readonly struct ExecActionRef
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly int _payload;

    internal ExecActionRef(AstArena? arena, int payload)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _payload = payload;
    }

    public bool HasValue => _arena is not null && _payload > 0;

    public StringRef Uses => HasValue ? new(ArenaChecked, ArenaChecked!.GetExecAction(_payload).Uses) : default;

    public TextRange? UsesKeyRange => HasValue ? ArenaChecked!.GetExecAction(_payload).UsesKeyRange : null;

    /// <summary>The <c>with:</c> inputs.</summary>
    public ActionInputRefMap Inputs => HasValue ? new(ArenaChecked, ArenaChecked!.GetExecAction(_payload).Inputs) : default;

    public StringRef Entrypoint => HasValue ? new(ArenaChecked, ArenaChecked!.GetExecAction(_payload).Entrypoint) : default;

    public StringRef Args => HasValue ? new(ArenaChecked, ArenaChecked!.GetExecAction(_payload).Args) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetExecAction(_payload).Range : default;
}

/// <summary>The payload of a <c>wait:</c> step.</summary>
public readonly struct ExecWaitRef
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly int _payload;

    internal ExecWaitRef(AstArena? arena, int payload)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _payload = payload;
    }

    public bool HasValue => _arena is not null && _payload > 0;

    public StringRefList Targets => HasValue ? new(ArenaChecked, ArenaChecked!.GetExecWait(_payload).Targets) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetExecWait(_payload).Range : default;
}

/// <summary>The payload of a <c>cancel:</c> step.</summary>
public readonly struct ExecCancelRef
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly int _payload;

    internal ExecCancelRef(AstArena? arena, int payload)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _payload = payload;
    }

    public bool HasValue => _arena is not null && _payload > 0;

    public StringRef Target => HasValue ? new(ArenaChecked, ArenaChecked!.GetExecCancel(_payload).Target) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetExecCancel(_payload).Range : default;
}

/// <summary>The payload of a <c>parallel:</c> step.</summary>
public readonly struct ExecParallelRef
{
    private readonly AstArena? _arena;
#if DEBUG
    private readonly int _generation;
#endif

    private AstArena? ArenaChecked
    {
        get
        {
#if DEBUG
            _arena?.AssertGeneration(_generation);
#endif
            return _arena;
        }
    }

    private readonly int _payload;

    internal ExecParallelRef(AstArena? arena, int payload)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _payload = payload;
    }

    public bool HasValue => _arena is not null && _payload > 0;

    public StepRefList Steps => HasValue ? new(ArenaChecked, ArenaChecked!.GetExecParallel(_payload).Steps) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetExecParallel(_payload).Range : default;
}
