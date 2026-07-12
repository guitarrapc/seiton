namespace Seiton.Core.Parsing.Ast;

/// <summary>The root of a parsed <c>action.yml</c> / <c>action.yaml</c> metadata document.</summary>
public readonly struct ActionMetadataRef
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

    private readonly ActionMetadata? _node;

    internal ActionMetadataRef(AstArena? arena, ActionMetadata? node)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    internal ActionMetadata? Node => _node;

    public StringRef Name => new(ArenaChecked, _node?.Name ?? default);

    public StringRef Description => new(ArenaChecked, _node?.Description ?? default);

    public ActionMetadataInputRefMap Inputs => new(ArenaChecked, _node?.Inputs ?? default);

    public ActionMetadataOutputRefMap Outputs => new(ArenaChecked, _node?.Outputs ?? default);

    public ActionMetadataRunsRef Runs => new(ArenaChecked, _node?.Runs ?? default);

    public ActionMetadataBrandingRef Branding => new(ArenaChecked, _node?.Branding ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>An input parameter declared in action metadata.</summary>
public readonly struct ActionMetadataInputRef
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

    private readonly ActionMetadataInputData _row;

    internal ActionMetadataInputRef(AstArena? arena, in ActionMetadataInputData row)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _row = row;
    }

    public bool HasValue => _arena is not null;

    public StringRef Name => new(ArenaChecked, _row.Name);

    public StringRef Description => new(ArenaChecked, _row.Description);

    public BoolRef Required => new(ArenaChecked, _row.Required);

    public StringRef Default => new(ArenaChecked, _row.Default);

    public StringRef DeprecationMessage => new(ArenaChecked, _row.DeprecationMessage);

    public TextRange Range => _row.Range;
}

/// <summary>An output parameter declared in action metadata.</summary>
public readonly struct ActionMetadataOutputRef
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

    private readonly ActionMetadataOutputData _row;

    internal ActionMetadataOutputRef(AstArena? arena, in ActionMetadataOutputData row)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _row = row;
    }

    public bool HasValue => _arena is not null;

    public StringRef Name => new(ArenaChecked, _row.Name);

    public StringRef Description => new(ArenaChecked, _row.Description);

    public StringRef Value => new(ArenaChecked, _row.Value);

    public TextRange Range => _row.Range;
}

/// <summary>The <c>runs:</c> section of action metadata defining the execution entry points.</summary>
public readonly struct ActionMetadataRunsRef
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

    private readonly ActionMetadataRunsId _id;

    internal ActionMetadataRunsRef(AstArena? arena, ActionMetadataRunsId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Using => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataRuns(_id).Using) : default;

    public StringRef Main => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataRuns(_id).Main) : default;

    public StringRef Pre => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataRuns(_id).Pre) : default;

    public StringRef Post => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataRuns(_id).Post) : default;

    public StringRef PreIf => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataRuns(_id).PreIf) : default;

    public StringRef PostIf => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataRuns(_id).PostIf) : default;

    public StringRef Image => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataRuns(_id).Image) : default;

    public StringRef Entrypoint => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataRuns(_id).Entrypoint) : default;

    public StringRefList Args => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataRuns(_id).Args) : default;

    public EnvRef Env => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataRuns(_id).Env) : default;

    public StepRefList Steps => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataRuns(_id).Steps) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetActionMetadataRuns(_id).Range : default;
}

/// <summary>The <c>branding:</c> section of action metadata (icon and color).</summary>
public readonly struct ActionMetadataBrandingRef
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

    private readonly ActionMetadataBrandingId _id;

    internal ActionMetadataBrandingRef(AstArena? arena, ActionMetadataBrandingId id)
    {
        _arena = arena;
#if DEBUG
        _generation = arena?.Generation ?? 0;
#endif
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Icon => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataBranding(_id).Icon) : default;

    public StringRef Color => HasValue ? new(ArenaChecked, ArenaChecked!.GetActionMetadataBranding(_id).Color) : default;

    public TextRange Range => HasValue ? ArenaChecked!.GetActionMetadataBranding(_id).Range : default;
}
