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

    public ActionMetadataInputRefMap Inputs => new(_arena, _node?.Inputs);

    public ActionMetadataOutputRefMap Outputs => new(_arena, _node?.Outputs);

    public ActionMetadataRunsRef Runs => new(_arena, _node?.Runs);

    public ActionMetadataBrandingRef Branding => new(_arena, _node?.Branding);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>An input parameter declared in action metadata.</summary>
public readonly struct ActionMetadataInputRef : INodeRef<ActionMetadataInput, ActionMetadataInputRef>
{
    private readonly AstArena? _arena;
    private readonly ActionMetadataInput? _node;

    internal ActionMetadataInputRef(AstArena? arena, ActionMetadataInput? node)
    {
        _arena = arena;
        _node = node;
    }

    static ActionMetadataInputRef INodeRef<ActionMetadataInput, ActionMetadataInputRef>.Create(AstArena? arena, ActionMetadataInput node) => new(arena, node);

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Name => new(_arena, _node?.Name ?? default);

    public StringRef Description => new(_arena, _node?.Description ?? default);

    public BoolRef Required => new(_arena, _node?.Required ?? default);

    public StringRef Default => new(_arena, _node?.Default ?? default);

    public StringRef DeprecationMessage => new(_arena, _node?.DeprecationMessage ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>An output parameter declared in action metadata.</summary>
public readonly struct ActionMetadataOutputRef : INodeRef<ActionMetadataOutput, ActionMetadataOutputRef>
{
    private readonly AstArena? _arena;
    private readonly ActionMetadataOutput? _node;

    internal ActionMetadataOutputRef(AstArena? arena, ActionMetadataOutput? node)
    {
        _arena = arena;
        _node = node;
    }

    static ActionMetadataOutputRef INodeRef<ActionMetadataOutput, ActionMetadataOutputRef>.Create(AstArena? arena, ActionMetadataOutput node) => new(arena, node);

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Name => new(_arena, _node?.Name ?? default);

    public StringRef Description => new(_arena, _node?.Description ?? default);

    public StringRef Value => new(_arena, _node?.Value ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The <c>runs:</c> section of action metadata defining the execution entry points.</summary>
public readonly struct ActionMetadataRunsRef
{
    private readonly AstArena? _arena;
    private readonly ActionMetadataRuns? _node;

    internal ActionMetadataRunsRef(AstArena? arena, ActionMetadataRuns? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Using => new(_arena, _node?.Using ?? default);

    public StringRef Main => new(_arena, _node?.Main ?? default);

    public StringRef Pre => new(_arena, _node?.Pre ?? default);

    public StringRef Post => new(_arena, _node?.Post ?? default);

    public StringRef PreIf => new(_arena, _node?.PreIf ?? default);

    public StringRef PostIf => new(_arena, _node?.PostIf ?? default);

    public StringRef Image => new(_arena, _node?.Image ?? default);

    public StringRef Entrypoint => new(_arena, _node?.Entrypoint ?? default);

    public StringRefList Args => new(_arena, _node?.Args ?? default);

    public EnvRef Env => new(_arena, _node?.Env);

    public StepRefList Steps => new(_arena, _node?.Steps);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>The <c>branding:</c> section of action metadata (icon and color).</summary>
public readonly struct ActionMetadataBrandingRef
{
    private readonly AstArena? _arena;
    private readonly ActionMetadataBranding? _node;

    internal ActionMetadataBrandingRef(AstArena? arena, ActionMetadataBranding? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Icon => new(_arena, _node?.Icon ?? default);

    public StringRef Color => new(_arena, _node?.Color ?? default);

    public TextRange Range => _node?.Range ?? default;
}
