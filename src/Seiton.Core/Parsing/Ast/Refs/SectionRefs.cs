namespace Seiton.Core.Parsing.Ast;

/// <summary>The <c>permissions:</c> block (either a single keyword or per-scope map).</summary>
public readonly struct PermissionsRef
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

    private readonly PermissionsId _id;

    internal PermissionsRef(AstArena? arena, PermissionsId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    /// <summary>The scalar form (<c>read-all</c> / <c>write-all</c>), if used.</summary>
    public StringRef All => HasValue ? new(ArenaChecked, ArenaChecked!.GetPermissions(_id).All) : default;

    public PermissionScopeRefMap Scopes => HasValue ? new(ArenaChecked, ArenaChecked!.GetPermissions(_id).Scopes) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetPermissions(_id).Range : default;
}

/// <summary>A single permission scope entry (e.g. <c>contents: read</c>).</summary>
public readonly struct PermissionScopeRef
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

    private readonly PermissionScopeData _row;

    internal PermissionScopeRef(AstArena? arena, in PermissionScopeData row)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _row = row;
    }

    public StringRef Name => new(ArenaChecked, _row.Name);

    /// <summary>The raw key text of the scope name.</summary>
    public KeyRef NameText => new(ArenaChecked, _row.NameText);

    public StringRef Value => new(ArenaChecked, _row.Value);

    /// <summary>The raw value text of the scope.</summary>
    public KeyRef ValueText => new(ArenaChecked, _row.ValueText);
}

/// <summary>The <c>env:</c> block (mapping form or whole-map expression form).</summary>
public readonly struct EnvRef
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

    private readonly EnvId _id;

    internal EnvRef(AstArena? arena, EnvId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    /// <summary>The whole-map <c>${{ }}</c> expression, if used instead of a mapping.</summary>
    public StringRef Expression => HasValue ? new(ArenaChecked, ArenaChecked!.GetEnv(_id).Expression) : default;

    public EnvVarRefMap Vars => HasValue ? new(ArenaChecked, ArenaChecked!.GetEnv(_id).Vars) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetEnv(_id).Range : default;
}

/// <summary>A single environment variable (key-value pair).</summary>
public readonly struct EnvVarRef
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

    private readonly EnvVarData _row;

    internal EnvVarRef(AstArena? arena, in EnvVarData row)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _row = row;
    }

    public StringRef Name => new(ArenaChecked, _row.Name);

    public StringRef Value => new(ArenaChecked, _row.Value);
}

/// <summary>The <c>defaults:</c> section.</summary>
public readonly struct DefaultsRef
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

    private readonly DefaultsId _id;

    internal DefaultsRef(AstArena? arena, DefaultsId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public DefaultsRunRef Run => HasValue ? new(ArenaChecked, ArenaChecked!.GetDefaults(_id).Run) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetDefaults(_id).Range : default;
}

/// <summary>The <c>defaults.run:</c> section (default shell and working directory).</summary>
public readonly struct DefaultsRunRef
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

    private readonly DefaultsRunId _id;

    internal DefaultsRunRef(AstArena? arena, DefaultsRunId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Shell => HasValue ? new(ArenaChecked, ArenaChecked!.GetDefaultsRun(_id).Shell) : default;

    public StringRef WorkingDirectory => HasValue ? new(ArenaChecked, ArenaChecked!.GetDefaultsRun(_id).WorkingDirectory) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetDefaultsRun(_id).Range : default;
}

/// <summary>The <c>concurrency:</c> block.</summary>
public readonly struct ConcurrencyRef
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

    private readonly ConcurrencyId _id;

    internal ConcurrencyRef(AstArena? arena, ConcurrencyId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Group => HasValue ? new(ArenaChecked, ArenaChecked!.GetConcurrency(_id).Group) : default;

    public BoolRef CancelInProgress => HasValue ? new(ArenaChecked, ArenaChecked!.GetConcurrency(_id).CancelInProgress) : default;

    public StringRef Queue => HasValue ? new(ArenaChecked, ArenaChecked!.GetConcurrency(_id).Queue) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetConcurrency(_id).Range : default;
}

/// <summary>The <c>environment:</c> block for deployment environments.</summary>
public readonly struct EnvironmentRef
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

    private readonly EnvironmentId _id;

    internal EnvironmentRef(AstArena? arena, EnvironmentId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Name => HasValue ? new(ArenaChecked, ArenaChecked!.GetEnvironment(_id).Name) : default;

    public StringRef Url => HasValue ? new(ArenaChecked, ArenaChecked!.GetEnvironment(_id).Url) : default;

    public BoolRef Deployment => HasValue ? new(ArenaChecked, ArenaChecked!.GetEnvironment(_id).Deployment) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetEnvironment(_id).Range : default;
}

/// <summary>The <c>runs-on:</c> specification for job runner selection.</summary>
public readonly struct RunnerRef
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

    private readonly RunnerId _id;

    internal RunnerRef(AstArena? arena, RunnerId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRefList Labels => HasValue ? new(ArenaChecked, ArenaChecked!.GetRunner(_id).Labels) : default;

    /// <summary>The whole-value <c>${{ }}</c> expression, if used for the labels.</summary>
    public StringRef LabelsExpr => HasValue ? new(ArenaChecked, ArenaChecked!.GetRunner(_id).LabelsExpr) : default;

    public StringRef Group => HasValue ? new(ArenaChecked, ArenaChecked!.GetRunner(_id).Group) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetRunner(_id).Range : default;
}

/// <summary>The <c>strategy:</c> block for job execution strategy.</summary>
public readonly struct StrategyRef
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

    private readonly StrategyId _id;

    internal StrategyRef(AstArena? arena, StrategyId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public MatrixRef Matrix => HasValue ? new(ArenaChecked, ArenaChecked!.GetStrategy(_id).Matrix) : default;

    public BoolRef FailFast => HasValue ? new(ArenaChecked, ArenaChecked!.GetStrategy(_id).FailFast) : default;

    public IntRef MaxParallel => HasValue ? new(ArenaChecked, ArenaChecked!.GetStrategy(_id).MaxParallel) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetStrategy(_id).Range : default;
}

/// <summary>The <c>matrix:</c> block defining build matrix dimensions.</summary>
public readonly struct MatrixRef
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

    private readonly MatrixId _id;

    internal MatrixRef(AstArena? arena, MatrixId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    /// <summary>The whole-matrix <c>${{ }}</c> expression, if used.</summary>
    public StringRef Expression => HasValue ? new(ArenaChecked, ArenaChecked!.GetMatrix(_id).Expression) : default;

    public CombinationsRefList Include => HasValue ? new(ArenaChecked, ArenaChecked!.GetMatrix(_id).Include) : default;

    public CombinationsRefList Exclude => HasValue ? new(ArenaChecked, ArenaChecked!.GetMatrix(_id).Exclude) : default;

    public MatrixRowRefMap Rows => HasValue ? new(ArenaChecked, ArenaChecked!.GetMatrix(_id).Rows) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetMatrix(_id).Range : default;
}

/// <summary>A single row (dimension) in a matrix definition.</summary>
public readonly struct MatrixRowRef
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

    private readonly MatrixRowData _row;

    internal MatrixRowRef(AstArena? arena, in MatrixRowData row)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _row = row;
    }

    public bool HasValue => _arena is not null;

    /// <summary>The whole-row <c>${{ }}</c> expression, if used.</summary>
    public StringRef Expression => new(ArenaChecked, _row.Expression);

    public RawYamlRefList Values => new(ArenaChecked, _row.Values);

    public StringRef Name => new(ArenaChecked, _row.Name);
}

/// <summary>Matrix include/exclude combination entries.</summary>
public readonly struct MatrixCombinationsRef
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

    private readonly MatrixCombinationsData _row;

    internal MatrixCombinationsRef(AstArena? arena, in MatrixCombinationsData row)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _row = row;
    }

    public bool HasValue => _arena is not null;

    /// <summary>The whole-block <c>${{ }}</c> expression, if used.</summary>
    public StringRef Expression => new(ArenaChecked, _row.Expression);

    public CombinationEntryRefList Entries => new(ArenaChecked, _row.Entries);
}

/// <summary>Discriminator for raw YAML value kinds.</summary>
public enum RawYamlKind
{
    None,
    String,
    Array,
    Object,
}

/// <summary>An unstructured YAML value (matrix entries), discriminated by <see cref="Kind"/>.</summary>
public readonly struct RawYamlRef
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

    private readonly RawYamlId _id;

    internal RawYamlRef(AstArena? arena, RawYamlId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public RawYamlKind Kind => HasValue ? ArenaChecked!.GetRawYaml(_id).Kind : RawYamlKind.None;

    /// <summary>The scalar value. Default when <see cref="Kind"/> is not <see cref="RawYamlKind.String"/>.</summary>
    public StringRef Scalar => HasValue ? new(ArenaChecked, ArenaChecked!.GetRawYaml(_id).Scalar) : default;

    /// <summary>The array items. Default when <see cref="Kind"/> is not <see cref="RawYamlKind.Array"/>.</summary>
    public RawYamlRefList Items => HasValue ? new(ArenaChecked, ArenaChecked!.GetRawYaml(_id).Items) : default;

    /// <summary>The mapping properties. Default when <see cref="Kind"/> is not <see cref="RawYamlKind.Object"/>.</summary>
    public RawYamlRefMap Properties => HasValue ? new(ArenaChecked, ArenaChecked!.GetRawYaml(_id).Properties) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetRawYaml(_id).Range : default;
}

/// <summary>The <c>container:</c> block for job containerization.</summary>
public readonly struct ContainerRef
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

    private readonly ContainerId _id;

    internal ContainerRef(AstArena? arena, ContainerId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Image => HasValue ? new(ArenaChecked, ArenaChecked!.GetContainer(_id).Image) : default;

    public CredentialsRef Credentials => HasValue ? new(ArenaChecked, ArenaChecked!.GetContainer(_id).Credentials) : default;

    public EnvRef Env => HasValue ? new(ArenaChecked, ArenaChecked!.GetContainer(_id).Env) : default;

    public StringRefList Ports => HasValue ? new(ArenaChecked, ArenaChecked!.GetContainer(_id).Ports) : default;

    public StringRefList Volumes => HasValue ? new(ArenaChecked, ArenaChecked!.GetContainer(_id).Volumes) : default;

    public StringRef Options => HasValue ? new(ArenaChecked, ArenaChecked!.GetContainer(_id).Options) : default;

    public StringRef Entrypoint => HasValue ? new(ArenaChecked, ArenaChecked!.GetContainer(_id).Entrypoint) : default;

    public StringRef Command => HasValue ? new(ArenaChecked, ArenaChecked!.GetContainer(_id).Command) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetContainer(_id).Range : default;
}

/// <summary>The <c>services:</c> block for job service containers.</summary>
public readonly struct ServicesRef
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

    private readonly ServicesId _id;

    internal ServicesRef(AstArena? arena, ServicesId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    /// <summary>The whole-map <c>${{ }}</c> expression, if used instead of a mapping.</summary>
    public StringRef Expression => HasValue ? new(ArenaChecked, ArenaChecked!.GetServices(_id).Expression) : default;

    public ServiceRefMap ServiceMap => HasValue ? new(ArenaChecked, ArenaChecked!.GetServices(_id).ServiceMap) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetServices(_id).Range : default;
}

/// <summary>A single service container definition.</summary>
public readonly struct ServiceRef
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

    private readonly ServiceData _row;

    internal ServiceRef(AstArena? arena, in ServiceData row)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _row = row;
    }

    public bool HasValue => _arena is not null;

    public StringRef Name => new(ArenaChecked, _row.Name);

    public ContainerRef Container => new(ArenaChecked, _row.Container);

    public TextRange Range => _row.Range;
}

/// <summary>Registry credentials for a container image.</summary>
public readonly struct CredentialsRef
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

    private readonly CredentialsId _id;

    internal CredentialsRef(AstArena? arena, CredentialsId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Username => HasValue ? new(ArenaChecked, ArenaChecked!.GetCredentials(_id).Username) : default;

    public StringRef Password => HasValue ? new(ArenaChecked, ArenaChecked!.GetCredentials(_id).Password) : default;

    /// <summary>The whole-block <c>${{ }}</c> expression, if used instead of a mapping.</summary>
    public StringRef Expression => HasValue ? new(ArenaChecked, ArenaChecked!.GetCredentials(_id).Expression) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetCredentials(_id).Range : default;
}

/// <summary>A reusable workflow call (<c>uses:</c> at job level).</summary>
public readonly struct WorkflowCallRef
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

    private readonly WorkflowCallId _id;

    internal WorkflowCallRef(AstArena? arena, WorkflowCallId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Uses => HasValue ? new(ArenaChecked, ArenaChecked!.GetWorkflowCall(_id).Uses) : default;

    public TextRange? UsesKeyRange => HasValue ? ArenaChecked!.GetWorkflowCall(_id).UsesKeyRange : null;

    public WorkflowCallInputRefMap Inputs => HasValue ? new(ArenaChecked, ArenaChecked!.GetWorkflowCall(_id).Inputs) : default;

    public TextRange? WithKeyRange => HasValue ? ArenaChecked!.GetWorkflowCall(_id).WithKeyRange : null;

    public WorkflowCallSecretRefMap Secrets => HasValue ? new(ArenaChecked, ArenaChecked!.GetWorkflowCall(_id).Secrets) : default;

    public TextRange? SecretsKeyRange => HasValue ? ArenaChecked!.GetWorkflowCall(_id).SecretsKeyRange : null;

    public bool InheritSecrets => HasValue && ArenaChecked!.GetWorkflowCall(_id).InheritSecrets;
}

/// <summary>An input passed to a reusable workflow call.</summary>
public readonly struct WorkflowCallInputRef
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

    private readonly WorkflowCallInputData _row;

    internal WorkflowCallInputRef(AstArena? arena, in WorkflowCallInputData row)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _row = row;
    }

    public StringRef Name => new(ArenaChecked, _row.Name);

    public StringRef Value => new(ArenaChecked, _row.Value);
}

/// <summary>A secret passed to a reusable workflow call.</summary>
public readonly struct WorkflowCallSecretRef
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

    private readonly WorkflowCallSecretData _row;

    internal WorkflowCallSecretRef(AstArena? arena, in WorkflowCallSecretData row)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _row = row;
    }

    public StringRef Name => new(ArenaChecked, _row.Name);

    public StringRef Value => new(ArenaChecked, _row.Value);
}

/// <summary>The <c>snapshot:</c> configuration of a job.</summary>
public readonly struct SnapshotRef
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

    private readonly SnapshotId _id;

    internal SnapshotRef(AstArena? arena, SnapshotId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Version => HasValue ? new(ArenaChecked, ArenaChecked!.GetSnapshot(_id).Version) : default;

    public StringRef ImageName => HasValue ? new(ArenaChecked, ArenaChecked!.GetSnapshot(_id).ImageName) : default;

    public StringRef If => HasValue ? new(ArenaChecked, ArenaChecked!.GetSnapshot(_id).If) : default;

    public TextRange? IfKeyRange => HasValue ? ArenaChecked!.GetSnapshot(_id).IfKeyRange : null;
}
