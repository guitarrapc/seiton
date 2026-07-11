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
    private readonly Defaults? _node;

    internal DefaultsRef(AstArena? arena, Defaults? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public DefaultsRunRef Run => new(_arena, _node?.Run);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The <c>defaults.run:</c> section (default shell and working directory).</summary>
public readonly struct DefaultsRunRef
{
    private readonly AstArena? _arena;
    private readonly DefaultsRun? _node;

    internal DefaultsRunRef(AstArena? arena, DefaultsRun? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Shell => new(_arena, _node?.Shell ?? default);

    public StringRef WorkingDirectory => new(_arena, _node?.WorkingDirectory ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The <c>concurrency:</c> block.</summary>
public readonly struct ConcurrencyRef
{
    private readonly AstArena? _arena;
    private readonly Concurrency? _node;

    internal ConcurrencyRef(AstArena? arena, Concurrency? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Group => new(_arena, _node?.Group ?? default);

    public BoolRef CancelInProgress => new(_arena, _node?.CancelInProgress ?? default);

    public StringRef Queue => new(_arena, _node?.Queue ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The <c>environment:</c> block for deployment environments.</summary>
public readonly struct EnvironmentRef
{
    private readonly AstArena? _arena;
    private readonly Environment? _node;

    internal EnvironmentRef(AstArena? arena, Environment? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Name => new(_arena, _node?.Name ?? default);

    public StringRef Url => new(_arena, _node?.Url ?? default);

    public BoolRef Deployment => new(_arena, _node?.Deployment ?? default);

    public TextRange Range => _node?.Range ?? default;
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

    public StringRefList Labels => new(_arena, _node?.Labels);

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

    public RawYamlRefList Values => new(_arena, _node?.Values);

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

    public CredentialsRef Credentials => new(_arena, _node?.Credentials);

    public EnvRef Env => new(_arena, _node?.Env);

    public StringRefList Ports => new(_arena, _node?.Ports);

    public StringRefList Volumes => new(_arena, _node?.Volumes);

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
    private readonly Credentials? _node;

    internal CredentialsRef(AstArena? arena, Credentials? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Username => new(_arena, _node?.Username ?? default);

    public StringRef Password => new(_arena, _node?.Password ?? default);

    /// <summary>The whole-block <c>${{ }}</c> expression, if used instead of a mapping.</summary>
    public StringRef Expression => new(_arena, _node?.Expression ?? default);

    public TextRange Range => _node?.Range ?? default;
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
    private readonly Snapshot? _node;

    internal SnapshotRef(AstArena? arena, Snapshot? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Version => new(_arena, _node?.Version ?? default);

    public StringRef ImageName => new(_arena, _node?.ImageName ?? default);

    public StringRef If => new(_arena, _node?.If ?? default);

    public TextRange? IfKeyRange => _node?.IfKeyRange;
}
