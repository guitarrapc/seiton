namespace Seiton.Core.Parsing.Ast;

/// <summary>The root of a parsed <c>action.yml</c> / <c>action.yaml</c> metadata document.</summary>
public readonly struct ActionMetadataRef
{
    private readonly AstArena? _arena;
    private readonly ActionMetadata? _node;

    internal ActionMetadataRef(AstArena? arena, ActionMetadata? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    internal ActionMetadata? Node => _node;

    public StringRef Name => new(_arena, _node?.Name ?? default);

    public StringRef Description => new(_arena, _node?.Description ?? default);

    public ActionMetadataInputRefMap Inputs => new(_arena, _node?.Inputs ?? default);

    public ActionMetadataOutputRefMap Outputs => new(_arena, _node?.Outputs ?? default);

    public ActionMetadataRunsRef Runs => new(_arena, _node?.Runs ?? default);

    public ActionMetadataBrandingRef Branding => new(_arena, _node?.Branding ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>An input parameter declared in action metadata.</summary>
public readonly struct ActionMetadataInputRef
{
    private readonly AstArena? _arena;
    private readonly ActionMetadataInputData _row;

    internal ActionMetadataInputRef(AstArena? arena, in ActionMetadataInputData row)
    {
        _arena = arena;
        _row = row;
    }

    public bool HasValue => _arena is not null;

    public StringRef Name => new(_arena, _row.Name);

    public StringRef Description => new(_arena, _row.Description);

    public BoolRef Required => new(_arena, _row.Required);

    public StringRef Default => new(_arena, _row.Default);

    public StringRef DeprecationMessage => new(_arena, _row.DeprecationMessage);

    public TextRange Range => _row.Range;
}

/// <summary>An output parameter declared in action metadata.</summary>
public readonly struct ActionMetadataOutputRef
{
    private readonly AstArena? _arena;
    private readonly ActionMetadataOutputData _row;

    internal ActionMetadataOutputRef(AstArena? arena, in ActionMetadataOutputData row)
    {
        _arena = arena;
        _row = row;
    }

    public bool HasValue => _arena is not null;

    public StringRef Name => new(_arena, _row.Name);

    public StringRef Description => new(_arena, _row.Description);

    public StringRef Value => new(_arena, _row.Value);

    public TextRange Range => _row.Range;
}

/// <summary>The <c>runs:</c> section of action metadata defining the execution entry points.</summary>
public readonly struct ActionMetadataRunsRef
{
    private readonly AstArena? _arena;
    private readonly ActionMetadataRunsId _id;

    internal ActionMetadataRunsRef(AstArena? arena, ActionMetadataRunsId id)
    {
        _arena = arena;
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Using => HasValue ? new(_arena, _arena!.GetActionMetadataRuns(_id).Using) : default;

    public StringRef Main => HasValue ? new(_arena, _arena!.GetActionMetadataRuns(_id).Main) : default;

    public StringRef Pre => HasValue ? new(_arena, _arena!.GetActionMetadataRuns(_id).Pre) : default;

    public StringRef Post => HasValue ? new(_arena, _arena!.GetActionMetadataRuns(_id).Post) : default;

    public StringRef PreIf => HasValue ? new(_arena, _arena!.GetActionMetadataRuns(_id).PreIf) : default;

    public StringRef PostIf => HasValue ? new(_arena, _arena!.GetActionMetadataRuns(_id).PostIf) : default;

    public StringRef Image => HasValue ? new(_arena, _arena!.GetActionMetadataRuns(_id).Image) : default;

    public StringRef Entrypoint => HasValue ? new(_arena, _arena!.GetActionMetadataRuns(_id).Entrypoint) : default;

    public StringRefList Args => HasValue ? new(_arena, _arena!.GetActionMetadataRuns(_id).Args) : default;

    public EnvRef Env => HasValue ? new(_arena, _arena!.GetActionMetadataRuns(_id).Env) : default;

    public StepRefList Steps => HasValue ? new(_arena, _arena!.GetActionMetadataRuns(_id).Steps) : default;

    public TextRange Range => HasValue ? _arena!.GetActionMetadataRuns(_id).Range : default;
}

/// <summary>The <c>branding:</c> section of action metadata (icon and color).</summary>
public readonly struct ActionMetadataBrandingRef
{
    private readonly AstArena? _arena;
    private readonly ActionMetadataBrandingId _id;

    internal ActionMetadataBrandingRef(AstArena? arena, ActionMetadataBrandingId id)
    {
        _arena = arena;
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Icon => HasValue ? new(_arena, _arena!.GetActionMetadataBranding(_id).Icon) : default;

    public StringRef Color => HasValue ? new(_arena, _arena!.GetActionMetadataBranding(_id).Color) : default;

    public TextRange Range => HasValue ? _arena!.GetActionMetadataBranding(_id).Range : default;
}
