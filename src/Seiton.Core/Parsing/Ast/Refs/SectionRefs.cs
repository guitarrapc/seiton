namespace Seiton.Core.Parsing.Ast;

/// <summary>The <c>permissions:</c> block (either a single keyword or per-scope map).</summary>
public readonly struct PermissionsRef
{
    private readonly AstArena? _arena;
    private readonly Permissions? _node;

    internal PermissionsRef(AstArena? arena, Permissions? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    /// <summary>The scalar form (<c>read-all</c> / <c>write-all</c>), if used.</summary>
    public StringRef All => new(_arena, _node?.All ?? default);

    public PermissionScopeRefMap Scopes => new(_arena, _node?.Scopes);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>A single permission scope entry (e.g. <c>contents: read</c>).</summary>
public readonly struct PermissionScopeRef : INodeRef<PermissionScope, PermissionScopeRef>
{
    private readonly AstArena? _arena;
    private readonly PermissionScope _node;

    internal PermissionScopeRef(AstArena? arena, PermissionScope node)
    {
        _arena = arena;
        _node = node;
    }

    static PermissionScopeRef INodeRef<PermissionScope, PermissionScopeRef>.Create(AstArena? arena, PermissionScope node) => new(arena, node);

    public StringRef Name => new(_arena, _node.Name);

    /// <summary>The raw key text of the scope name.</summary>
    public KeyRef NameText => new(_arena, _node.NameText);

    public StringRef Value => new(_arena, _node.Value);

    /// <summary>The raw value text of the scope.</summary>
    public KeyRef ValueText => new(_arena, _node.ValueText);
}

/// <summary>The <c>env:</c> block (mapping form or whole-map expression form).</summary>
public readonly struct EnvRef
{
    private readonly AstArena? _arena;
    private readonly Env? _node;

    internal EnvRef(AstArena? arena, Env? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    /// <summary>The whole-map <c>${{ }}</c> expression, if used instead of a mapping.</summary>
    public StringRef Expression => new(_arena, _node?.Expression ?? default);

    public EnvVarRefMap Vars => new(_arena, _node?.Vars);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>A single environment variable (key-value pair).</summary>
public readonly struct EnvVarRef : INodeRef<EnvVar, EnvVarRef>
{
    private readonly AstArena? _arena;
    private readonly EnvVar _node;

    internal EnvVarRef(AstArena? arena, EnvVar node)
    {
        _arena = arena;
        _node = node;
    }

    static EnvVarRef INodeRef<EnvVar, EnvVarRef>.Create(AstArena? arena, EnvVar node) => new(arena, node);

    public StringRef Name => new(_arena, _node.Name);

    public StringRef Value => new(_arena, _node.Value);
}

/// <summary>The <c>defaults:</c> section.</summary>
public readonly struct DefaultsRef
{
    private readonly AstArena? _arena;
    private readonly DefaultsId _id;

    internal DefaultsRef(AstArena? arena, DefaultsId id)
    {
        _arena = arena;
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public DefaultsRunRef Run => HasValue ? new(_arena, _arena!.GetDefaults(_id).Run) : default;

    public TextRange Range => HasValue ? _arena!.GetDefaults(_id).Range : default;
}

/// <summary>The <c>defaults.run:</c> section (default shell and working directory).</summary>
public readonly struct DefaultsRunRef
{
    private readonly AstArena? _arena;
    private readonly DefaultsRunId _id;

    internal DefaultsRunRef(AstArena? arena, DefaultsRunId id)
    {
        _arena = arena;
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Shell => HasValue ? new(_arena, _arena!.GetDefaultsRun(_id).Shell) : default;

    public StringRef WorkingDirectory => HasValue ? new(_arena, _arena!.GetDefaultsRun(_id).WorkingDirectory) : default;

    public TextRange Range => HasValue ? _arena!.GetDefaultsRun(_id).Range : default;
}

/// <summary>The <c>concurrency:</c> block.</summary>
public readonly struct ConcurrencyRef
{
    private readonly AstArena? _arena;
    private readonly ConcurrencyId _id;

    internal ConcurrencyRef(AstArena? arena, ConcurrencyId id)
    {
        _arena = arena;
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Group => HasValue ? new(_arena, _arena!.GetConcurrency(_id).Group) : default;

    public BoolRef CancelInProgress => HasValue ? new(_arena, _arena!.GetConcurrency(_id).CancelInProgress) : default;

    public StringRef Queue => HasValue ? new(_arena, _arena!.GetConcurrency(_id).Queue) : default;

    public TextRange Range => HasValue ? _arena!.GetConcurrency(_id).Range : default;
}

/// <summary>The <c>environment:</c> block for deployment environments.</summary>
public readonly struct EnvironmentRef
{
    private readonly AstArena? _arena;
    private readonly EnvironmentId _id;

    internal EnvironmentRef(AstArena? arena, EnvironmentId id)
    {
        _arena = arena;
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Name => HasValue ? new(_arena, _arena!.GetEnvironment(_id).Name) : default;

    public StringRef Url => HasValue ? new(_arena, _arena!.GetEnvironment(_id).Url) : default;

    public BoolRef Deployment => HasValue ? new(_arena, _arena!.GetEnvironment(_id).Deployment) : default;

    public TextRange Range => HasValue ? _arena!.GetEnvironment(_id).Range : default;
}

/// <summary>The <c>runs-on:</c> specification for job runner selection.</summary>
public readonly struct RunnerRef
{
    private readonly AstArena? _arena;
    private readonly Runner? _node;

    internal RunnerRef(AstArena? arena, Runner? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRefList Labels => new(_arena, _node?.Labels ?? default);

    /// <summary>The whole-value <c>${{ }}</c> expression, if used for the labels.</summary>
    public StringRef LabelsExpr => new(_arena, _node?.LabelsExpr ?? default);

    public StringRef Group => new(_arena, _node?.Group ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The <c>strategy:</c> block for job execution strategy.</summary>
public readonly struct StrategyRef
{
    private readonly AstArena? _arena;
    private readonly Strategy? _node;

    internal StrategyRef(AstArena? arena, Strategy? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public MatrixRef Matrix => new(_arena, _node?.Matrix);

    public BoolRef FailFast => new(_arena, _node?.FailFast ?? default);

    public IntRef MaxParallel => new(_arena, _node?.MaxParallel ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The <c>matrix:</c> block defining build matrix dimensions.</summary>
public readonly struct MatrixRef
{
    private readonly AstArena? _arena;
    private readonly Matrix? _node;

    internal MatrixRef(AstArena? arena, Matrix? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    /// <summary>The whole-matrix <c>${{ }}</c> expression, if used.</summary>
    public StringRef Expression => new(_arena, _node?.Expression ?? default);

    public CombinationsRefList Include => new(_arena, _node?.Include);

    public CombinationsRefList Exclude => new(_arena, _node?.Exclude);

    public MatrixRowRefMap Rows => new(_arena, _node?.Rows);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>A single row (dimension) in a matrix definition.</summary>
public readonly struct MatrixRowRef : INodeRef<MatrixRow, MatrixRowRef>
{
    private readonly AstArena? _arena;
    private readonly MatrixRow? _node;

    internal MatrixRowRef(AstArena? arena, MatrixRow? node)
    {
        _arena = arena;
        _node = node;
    }

    static MatrixRowRef INodeRef<MatrixRow, MatrixRowRef>.Create(AstArena? arena, MatrixRow node) => new(arena, node);

    public bool HasValue => _node is not null && _arena is not null;

    /// <summary>The whole-row <c>${{ }}</c> expression, if used.</summary>
    public StringRef Expression => new(_arena, _node?.Expression ?? default);

    public RawYamlRefList Values => new(_arena, _node?.Values ?? default);

    public StringRef Name => new(_arena, _node?.Name ?? default);
}

/// <summary>Matrix include/exclude combination entries.</summary>
public readonly struct MatrixCombinationsRef
{
    private readonly AstArena? _arena;
    private readonly MatrixCombinations? _node;

    internal MatrixCombinationsRef(AstArena? arena, MatrixCombinations? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    /// <summary>The whole-block <c>${{ }}</c> expression, if used.</summary>
    public StringRef Expression => new(_arena, _node?.Expression ?? default);

    public CombinationEntryRefList Entries => new(_arena, _node?.Entries);
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
public readonly struct RawYamlRef : INodeRef<RawYamlValue, RawYamlRef>
{
    private readonly AstArena? _arena;
    private readonly RawYamlValue? _node;

    internal RawYamlRef(AstArena? arena, RawYamlValue? node)
    {
        _arena = arena;
        _node = node;
    }

    static RawYamlRef INodeRef<RawYamlValue, RawYamlRef>.Create(AstArena? arena, RawYamlValue node) => new(arena, node);

    public bool HasValue => _node is not null && _arena is not null;

    public RawYamlKind Kind => _node switch
    {
        RawYamlString => RawYamlKind.String,
        RawYamlArray => RawYamlKind.Array,
        RawYamlObject => RawYamlKind.Object,
        _ => RawYamlKind.None,
    };

    /// <summary>The scalar value. Default when <see cref="Kind"/> is not <see cref="RawYamlKind.String"/>.</summary>
    public StringRef Scalar => new(_arena, (_node as RawYamlString)?.Value ?? default);

    /// <summary>The array items. Default when <see cref="Kind"/> is not <see cref="RawYamlKind.Array"/>.</summary>
    public RawYamlRefList Items => new(_arena, (_node as RawYamlArray)?.Items);

    /// <summary>The mapping properties. Default when <see cref="Kind"/> is not <see cref="RawYamlKind.Object"/>.</summary>
    public RawYamlRefMap Properties => new(_arena, (_node as RawYamlObject)?.Properties);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The <c>container:</c> block for job containerization.</summary>
public readonly struct ContainerRef
{
    private readonly AstArena? _arena;
    private readonly Container? _node;

    internal ContainerRef(AstArena? arena, Container? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Image => new(_arena, _node?.Image ?? default);

    public CredentialsRef Credentials => new(_arena, _node?.Credentials ?? default);

    public EnvRef Env => new(_arena, _node?.Env);

    public StringRefList Ports => new(_arena, _node?.Ports ?? default);

    public StringRefList Volumes => new(_arena, _node?.Volumes ?? default);

    public StringRef Options => new(_arena, _node?.Options ?? default);

    public StringRef Entrypoint => new(_arena, _node?.Entrypoint ?? default);

    public StringRef Command => new(_arena, _node?.Command ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The <c>services:</c> block for job service containers.</summary>
public readonly struct ServicesRef
{
    private readonly AstArena? _arena;
    private readonly Services? _node;

    internal ServicesRef(AstArena? arena, Services? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    /// <summary>The whole-map <c>${{ }}</c> expression, if used instead of a mapping.</summary>
    public StringRef Expression => new(_arena, _node?.Expression ?? default);

    public ServiceRefMap ServiceMap => new(_arena, _node?.ServiceMap);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>A single service container definition.</summary>
public readonly struct ServiceRef : INodeRef<Service, ServiceRef>
{
    private readonly AstArena? _arena;
    private readonly Service? _node;

    internal ServiceRef(AstArena? arena, Service? node)
    {
        _arena = arena;
        _node = node;
    }

    static ServiceRef INodeRef<Service, ServiceRef>.Create(AstArena? arena, Service node) => new(arena, node);

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Name => new(_arena, _node?.Name ?? default);

    public ContainerRef Container => new(_arena, _node?.Container);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>Registry credentials for a container image.</summary>
public readonly struct CredentialsRef
{
    private readonly AstArena? _arena;
    private readonly CredentialsId _id;

    internal CredentialsRef(AstArena? arena, CredentialsId id)
    {
        _arena = arena;
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Username => HasValue ? new(_arena, _arena!.GetCredentials(_id).Username) : default;

    public StringRef Password => HasValue ? new(_arena, _arena!.GetCredentials(_id).Password) : default;

    /// <summary>The whole-block <c>${{ }}</c> expression, if used instead of a mapping.</summary>
    public StringRef Expression => HasValue ? new(_arena, _arena!.GetCredentials(_id).Expression) : default;

    public TextRange Range => HasValue ? _arena!.GetCredentials(_id).Range : default;
}

/// <summary>A reusable workflow call (<c>uses:</c> at job level).</summary>
public readonly struct WorkflowCallRef
{
    private readonly AstArena? _arena;
    private readonly WorkflowCall? _node;

    internal WorkflowCallRef(AstArena? arena, WorkflowCall? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    internal WorkflowCall? Node => _node;

    public StringRef Uses => new(_arena, _node?.Uses ?? default);

    public TextRange? UsesKeyRange => _node?.UsesKeyRange;

    public WorkflowCallInputRefMap Inputs => new(_arena, _node?.Inputs);

    public TextRange? WithKeyRange => _node?.WithKeyRange;

    public WorkflowCallSecretRefMap Secrets => new(_arena, _node?.Secrets);

    public TextRange? SecretsKeyRange => _node?.SecretsKeyRange;

    public bool InheritSecrets => _node?.InheritSecrets ?? false;
}

/// <summary>An input passed to a reusable workflow call.</summary>
public readonly struct WorkflowCallInputRef : INodeRef<WorkflowCallInput, WorkflowCallInputRef>
{
    private readonly AstArena? _arena;
    private readonly WorkflowCallInput _node;

    internal WorkflowCallInputRef(AstArena? arena, WorkflowCallInput node)
    {
        _arena = arena;
        _node = node;
    }

    static WorkflowCallInputRef INodeRef<WorkflowCallInput, WorkflowCallInputRef>.Create(AstArena? arena, WorkflowCallInput node) => new(arena, node);

    public StringRef Name => new(_arena, _node.Name);

    public StringRef Value => new(_arena, _node.Value);
}

/// <summary>A secret passed to a reusable workflow call.</summary>
public readonly struct WorkflowCallSecretRef : INodeRef<WorkflowCallSecret, WorkflowCallSecretRef>
{
    private readonly AstArena? _arena;
    private readonly WorkflowCallSecret _node;

    internal WorkflowCallSecretRef(AstArena? arena, WorkflowCallSecret node)
    {
        _arena = arena;
        _node = node;
    }

    static WorkflowCallSecretRef INodeRef<WorkflowCallSecret, WorkflowCallSecretRef>.Create(AstArena? arena, WorkflowCallSecret node) => new(arena, node);

    public StringRef Name => new(_arena, _node.Name);

    public StringRef Value => new(_arena, _node.Value);
}

/// <summary>The <c>snapshot:</c> configuration of a job.</summary>
public readonly struct SnapshotRef
{
    private readonly AstArena? _arena;
    private readonly SnapshotId _id;

    internal SnapshotRef(AstArena? arena, SnapshotId id)
    {
        _arena = arena;
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Version => HasValue ? new(_arena, _arena!.GetSnapshot(_id).Version) : default;

    public StringRef ImageName => HasValue ? new(_arena, _arena!.GetSnapshot(_id).ImageName) : default;

    public StringRef If => HasValue ? new(_arena, _arena!.GetSnapshot(_id).If) : default;

    public TextRange? IfKeyRange => HasValue ? _arena!.GetSnapshot(_id).IfKeyRange : null;
}
