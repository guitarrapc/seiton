namespace Seiton.Core.Parsing.Ast;

/// <summary>Discriminator for trigger event kinds in the <c>on:</c> section.</summary>
public enum EventKind
{
    None,
    Webhook,
    Scheduled,
    WorkflowDispatch,
    WorkflowCall,
    RepositoryDispatch,
    ImageVersion,
}

/// <summary>A trigger event in the <c>on:</c> section, discriminated by <see cref="Kind"/>.</summary>
public readonly struct EventRef
{
    private readonly AstArena? _arena;
    private readonly Event? _node;

    internal EventRef(AstArena? arena, Event? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    internal Event? Node => _node;

    public EventKind Kind => _node switch
    {
        WebhookEvent => EventKind.Webhook,
        ScheduledEvent => EventKind.Scheduled,
        WorkflowDispatchEvent => EventKind.WorkflowDispatch,
        WorkflowCallEvent => EventKind.WorkflowCall,
        RepositoryDispatchEvent => EventKind.RepositoryDispatch,
        ImageVersionEvent => EventKind.ImageVersion,
        _ => EventKind.None,
    };

    public StringRef EventName => new(_arena, _node?.EventName ?? default);

    public TextRange Range => _node?.Range ?? default;

    /// <summary>The webhook payload. Default when <see cref="Kind"/> is not <see cref="EventKind.Webhook"/>.</summary>
    public WebhookEventRef AsWebhook() => new(_arena, _node as WebhookEvent);

    /// <summary>The schedule payload. Default when <see cref="Kind"/> is not <see cref="EventKind.Scheduled"/>.</summary>
    public ScheduledEventRef AsScheduled() => new(_arena, _node as ScheduledEvent);

    /// <summary>The dispatch payload. Default when <see cref="Kind"/> is not <see cref="EventKind.WorkflowDispatch"/>.</summary>
    public WorkflowDispatchEventRef AsWorkflowDispatch() => new(_arena, _node as WorkflowDispatchEvent);

    /// <summary>The workflow_call payload. Default when <see cref="Kind"/> is not <see cref="EventKind.WorkflowCall"/>.</summary>
    public WorkflowCallEventRef AsWorkflowCall() => new(_arena, _node as WorkflowCallEvent);

    /// <summary>The repository_dispatch payload. Default when <see cref="Kind"/> is not <see cref="EventKind.RepositoryDispatch"/>.</summary>
    public RepositoryDispatchEventRef AsRepositoryDispatch() => new(_arena, _node as RepositoryDispatchEvent);

    /// <summary>The image_version payload. Default when <see cref="Kind"/> is not <see cref="EventKind.ImageVersion"/>.</summary>
    public ImageVersionEventRef AsImageVersion() => new(_arena, _node as ImageVersionEvent);
}

/// <summary>A webhook-triggered event (e.g. <c>push</c>, <c>pull_request</c>).</summary>
public readonly struct WebhookEventRef
{
    private readonly AstArena? _arena;
    private readonly WebhookEvent? _node;

    internal WebhookEventRef(AstArena? arena, WebhookEvent? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Hook => new(_arena, _node?.Hook ?? default);

    public StringRefList Types => new(_arena, _node?.Types ?? default);

    public WebhookEventFilterRef Branches => new(_arena, _node?.Branches);

    public WebhookEventFilterRef BranchesIgnore => new(_arena, _node?.BranchesIgnore);

    public WebhookEventFilterRef Tags => new(_arena, _node?.Tags);

    public WebhookEventFilterRef TagsIgnore => new(_arena, _node?.TagsIgnore);

    public WebhookEventFilterRef Paths => new(_arena, _node?.Paths);

    public WebhookEventFilterRef PathsIgnore => new(_arena, _node?.PathsIgnore);

    public StringRefList Workflows => new(_arena, _node?.Workflows ?? default);
}

/// <summary>A branch/path/tag filter within a webhook event.</summary>
public readonly struct WebhookEventFilterRef
{
    private readonly AstArena? _arena;
    private readonly WebhookEventFilter? _node;

    internal WebhookEventFilterRef(AstArena? arena, WebhookEventFilter? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Name => new(_arena, _node?.Name ?? default);

    public StringRefList Values => new(_arena, _node?.Values ?? default);
}

/// <summary>A <c>schedule:</c> event containing cron entries.</summary>
public readonly struct ScheduledEventRef
{
    private readonly AstArena? _arena;
    private readonly ScheduledEvent? _node;

    internal ScheduledEventRef(AstArena? arena, ScheduledEvent? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public ScheduleRefList Schedules => new(_arena, _node?.Schedules);
}

/// <summary>A single cron schedule entry.</summary>
public readonly struct ScheduleEntryRef
{
    private readonly AstArena? _arena;
    private readonly ScheduleEntry _node;

    internal ScheduleEntryRef(AstArena? arena, ScheduleEntry node)
    {
        _arena = arena;
        _node = node;
    }

    public StringRef Cron => new(_arena, _node.Cron);

    public StringRef Timezone => new(_arena, _node.Timezone);

    public TextRange Range => _node.Range;
}

/// <summary>A <c>workflow_dispatch:</c> event with optional inputs.</summary>
public readonly struct WorkflowDispatchEventRef
{
    private readonly AstArena? _arena;
    private readonly WorkflowDispatchEvent? _node;

    internal WorkflowDispatchEventRef(AstArena? arena, WorkflowDispatchEvent? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public DispatchInputRefMap Inputs => new(_arena, _node?.Inputs);
}

/// <summary>An input parameter for a <c>workflow_dispatch</c> event.</summary>
public readonly struct DispatchInputRef : INodeRef<DispatchInput, DispatchInputRef>
{
    private readonly AstArena? _arena;
    private readonly DispatchInput? _node;

    internal DispatchInputRef(AstArena? arena, DispatchInput? node)
    {
        _arena = arena;
        _node = node;
    }

    static DispatchInputRef INodeRef<DispatchInput, DispatchInputRef>.Create(AstArena? arena, DispatchInput node) => new(arena, node);

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Name => new(_arena, _node?.Name ?? default);

    public StringRef Description => new(_arena, _node?.Description ?? default);

    public BoolRef Required => new(_arena, _node?.Required ?? default);

    public StringRef Default => new(_arena, _node?.Default ?? default);

    public DispatchInputType Type => _node?.Type ?? DispatchInputType.None;

    public StringRefList Options => new(_arena, _node?.Options ?? default);

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>A <c>workflow_call:</c> event defining reusable workflow inputs/outputs/secrets.</summary>
public readonly struct WorkflowCallEventRef
{
    private readonly AstArena? _arena;
    private readonly WorkflowCallEvent? _node;

    internal WorkflowCallEventRef(AstArena? arena, WorkflowCallEvent? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    internal WorkflowCallEvent? Node => _node;

    public WorkflowCallEventInputRefList Inputs => new(_arena, _node?.Inputs);

    public WorkflowCallEventSecretRefMap Secrets => new(_arena, _node?.Secrets);

    public WorkflowCallEventOutputRefMap Outputs => new(_arena, _node?.Outputs);
}

/// <summary>An input declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventInputRef
{
    private readonly AstArena? _arena;
    private readonly WorkflowCallEventInput? _node;

    internal WorkflowCallEventInputRef(AstArena? arena, WorkflowCallEventInput? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRef Name => new(_arena, _node?.Name ?? default);

    /// <summary>The lower-cased input identifier.</summary>
    public Utf8String Id => _node?.Id ?? default;

    public StringRef Description => new(_arena, _node?.Description ?? default);

    public BoolRef Required => new(_arena, _node?.Required ?? default);

    public StringRef Default => new(_arena, _node?.Default ?? default);

    public WorkflowCallInputType Type => _node?.Type ?? WorkflowCallInputType.Invalid;

    public TextRange Range => _node?.Range ?? default;
}

/// <summary>A secret declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventSecretRef : INodeRef<WorkflowCallEventSecret, WorkflowCallEventSecretRef>
{
    private readonly AstArena? _arena;
    private readonly WorkflowCallEventSecret _node;

    internal WorkflowCallEventSecretRef(AstArena? arena, WorkflowCallEventSecret node)
    {
        _arena = arena;
        _node = node;
    }

    static WorkflowCallEventSecretRef INodeRef<WorkflowCallEventSecret, WorkflowCallEventSecretRef>.Create(AstArena? arena, WorkflowCallEventSecret node) => new(arena, node);

    public StringRef Name => new(_arena, _node.Name);

    public StringRef Description => new(_arena, _node.Description);

    public BoolRef Required => new(_arena, _node.Required);

    public TextRange Range => _node.Range;
}

/// <summary>An output declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventOutputRef : INodeRef<WorkflowCallEventOutput, WorkflowCallEventOutputRef>
{
    private readonly AstArena? _arena;
    private readonly WorkflowCallEventOutput _node;

    internal WorkflowCallEventOutputRef(AstArena? arena, WorkflowCallEventOutput node)
    {
        _arena = arena;
        _node = node;
    }

    static WorkflowCallEventOutputRef INodeRef<WorkflowCallEventOutput, WorkflowCallEventOutputRef>.Create(AstArena? arena, WorkflowCallEventOutput node) => new(arena, node);

    public StringRef Name => new(_arena, _node.Name);

    public StringRef Description => new(_arena, _node.Description);

    public StringRef Value => new(_arena, _node.Value);

    public TextRange Range => _node.Range;
}

/// <summary>A <c>repository_dispatch:</c> event with optional activity types.</summary>
public readonly struct RepositoryDispatchEventRef
{
    private readonly AstArena? _arena;
    private readonly RepositoryDispatchEvent? _node;

    internal RepositoryDispatchEventRef(AstArena? arena, RepositoryDispatchEvent? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRefList Types => new(_arena, _node?.Types ?? default);
}

/// <summary>An image version event (e.g. container image update triggers).</summary>
public readonly struct ImageVersionEventRef
{
    private readonly AstArena? _arena;
    private readonly ImageVersionEvent? _node;

    internal ImageVersionEventRef(AstArena? arena, ImageVersionEvent? node)
    {
        _arena = arena;
        _node = node;
    }

    public bool HasValue => _node is not null && _arena is not null;

    public StringRefList Names => new(_arena, _node?.Names ?? default);

    public StringRefList Versions => new(_arena, _node?.Versions ?? default);
}
