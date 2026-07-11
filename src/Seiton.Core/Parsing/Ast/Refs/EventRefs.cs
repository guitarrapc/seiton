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
    // 1-based absolute index into the event header table (0 = absent).
    private readonly int _raw;

    internal EventRef(AstArena? arena, int index)
    {
        _arena = arena;
        _raw = index + 1;
    }

    public bool HasValue => _arena is not null && _raw > 0;

    public EventKind Kind => HasValue ? _arena!.GetEvent(_raw - 1).Kind : EventKind.None;

    public StringRef EventName => HasValue ? new(_arena, _arena!.GetEvent(_raw - 1).EventName) : default;

    public TextRange Range => HasValue ? _arena!.GetEvent(_raw - 1).Range : default;

    private int Payload => HasValue ? _arena!.GetEvent(_raw - 1).Payload : 0;

    /// <summary>The webhook payload. Default when <see cref="Kind"/> is not <see cref="EventKind.Webhook"/>.</summary>
    public WebhookEventRef AsWebhook() => Kind == EventKind.Webhook && Payload > 0 ? new(_arena, Payload) : default;

    /// <summary>The schedule payload. Default when <see cref="Kind"/> is not <see cref="EventKind.Scheduled"/>.</summary>
    public ScheduledEventRef AsScheduled() => Kind == EventKind.Scheduled && Payload > 0 ? new(_arena, Payload) : default;

    /// <summary>The dispatch payload. Default when <see cref="Kind"/> is not <see cref="EventKind.WorkflowDispatch"/>.</summary>
    public WorkflowDispatchEventRef AsWorkflowDispatch() => Kind == EventKind.WorkflowDispatch && Payload > 0 ? new(_arena, Payload) : default;

    /// <summary>The workflow_call payload. Default when <see cref="Kind"/> is not <see cref="EventKind.WorkflowCall"/>.</summary>
    public WorkflowCallEventRef AsWorkflowCall() => Kind == EventKind.WorkflowCall && Payload > 0 ? new(_arena, Payload) : default;

    /// <summary>The repository_dispatch payload. Default when <see cref="Kind"/> is not <see cref="EventKind.RepositoryDispatch"/>.</summary>
    public RepositoryDispatchEventRef AsRepositoryDispatch() => Kind == EventKind.RepositoryDispatch && Payload > 0 ? new(_arena, Payload) : default;

    /// <summary>The image_version payload. Default when <see cref="Kind"/> is not <see cref="EventKind.ImageVersion"/>.</summary>
    public ImageVersionEventRef AsImageVersion() => Kind == EventKind.ImageVersion && Payload > 0 ? new(_arena, Payload) : default;
}

/// <summary>A webhook-triggered event (e.g. <c>push</c>, <c>pull_request</c>).</summary>
public readonly struct WebhookEventRef
{
    private readonly AstArena? _arena;
    private readonly int _payload;

    internal WebhookEventRef(AstArena? arena, int payload)
    {
        _arena = arena;
        _payload = payload;
    }

    public bool HasValue => _arena is not null && _payload > 0;

    public StringRef Hook => HasValue ? new(_arena, _arena!.GetWebhookEvent(_payload).Hook) : default;

    public StringRefList Types => HasValue ? new(_arena, _arena!.GetWebhookEvent(_payload).Types) : default;

    public WebhookEventFilterRef Branches => HasValue ? new(_arena, _arena!.GetWebhookEvent(_payload).Branches) : default;

    public WebhookEventFilterRef BranchesIgnore => HasValue ? new(_arena, _arena!.GetWebhookEvent(_payload).BranchesIgnore) : default;

    public WebhookEventFilterRef Tags => HasValue ? new(_arena, _arena!.GetWebhookEvent(_payload).Tags) : default;

    public WebhookEventFilterRef TagsIgnore => HasValue ? new(_arena, _arena!.GetWebhookEvent(_payload).TagsIgnore) : default;

    public WebhookEventFilterRef Paths => HasValue ? new(_arena, _arena!.GetWebhookEvent(_payload).Paths) : default;

    public WebhookEventFilterRef PathsIgnore => HasValue ? new(_arena, _arena!.GetWebhookEvent(_payload).PathsIgnore) : default;

    public StringRefList Workflows => HasValue ? new(_arena, _arena!.GetWebhookEvent(_payload).Workflows) : default;
}

/// <summary>A branch/path/tag filter within a webhook event.</summary>
public readonly struct WebhookEventFilterRef
{
    private readonly AstArena? _arena;
    private readonly WebhookFilterId _id;

    internal WebhookEventFilterRef(AstArena? arena, WebhookFilterId id)
    {
        _arena = arena;
        _id = id;
    }

    public bool HasValue => _arena is not null && _id.HasValue;

    public StringRef Name => HasValue ? new(_arena, _arena!.GetWebhookFilter(_id).Name) : default;

    public StringRefList Values => HasValue ? new(_arena, _arena!.GetWebhookFilter(_id).Values) : default;
}

/// <summary>A <c>schedule:</c> event containing cron entries.</summary>
public readonly struct ScheduledEventRef
{
    private readonly AstArena? _arena;
    private readonly int _payload;

    internal ScheduledEventRef(AstArena? arena, int payload)
    {
        _arena = arena;
        _payload = payload;
    }

    public bool HasValue => _arena is not null && _payload > 0;

    public ScheduleRefList Schedules => HasValue ? new(_arena, _arena!.GetScheduledEvent(_payload).Schedules) : default;
}

/// <summary>A single cron schedule entry.</summary>
public readonly struct ScheduleEntryRef
{
    private readonly AstArena? _arena;
    private readonly ScheduleEntry _row;

    internal ScheduleEntryRef(AstArena? arena, in ScheduleEntry row)
    {
        _arena = arena;
        _row = row;
    }

    public StringRef Cron => new(_arena, _row.Cron);

    public StringRef Timezone => new(_arena, _row.Timezone);

    public TextRange Range => _row.Range;
}

/// <summary>A <c>workflow_dispatch:</c> event with optional inputs.</summary>
public readonly struct WorkflowDispatchEventRef
{
    private readonly AstArena? _arena;
    private readonly int _payload;

    internal WorkflowDispatchEventRef(AstArena? arena, int payload)
    {
        _arena = arena;
        _payload = payload;
    }

    public bool HasValue => _arena is not null && _payload > 0;

    public DispatchInputRefMap Inputs => HasValue ? new(_arena, _arena!.GetWorkflowDispatchEvent(_payload).Inputs) : default;
}

/// <summary>An input parameter for a <c>workflow_dispatch</c> event.</summary>
public readonly struct DispatchInputRef
{
    private readonly AstArena? _arena;
    private readonly DispatchInputData _row;

    internal DispatchInputRef(AstArena? arena, in DispatchInputData row)
    {
        _arena = arena;
        _row = row;
    }

    public bool HasValue => _arena is not null;

    public StringRef Name => new(_arena, _row.Name);

    public StringRef Description => new(_arena, _row.Description);

    public BoolRef Required => new(_arena, _row.Required);

    public StringRef Default => new(_arena, _row.Default);

    public DispatchInputType Type => _row.Type;

    public StringRefList Options => new(_arena, _row.Options);

    public TextRange Range => _row.Range;
}

/// <summary>A <c>workflow_call:</c> event defining reusable workflow inputs/outputs/secrets.</summary>
public readonly struct WorkflowCallEventRef
{
    private readonly AstArena? _arena;
    private readonly int _payload;

    internal WorkflowCallEventRef(AstArena? arena, int payload)
    {
        _arena = arena;
        _payload = payload;
    }

    public bool HasValue => _arena is not null && _payload > 0;

    public WorkflowCallEventInputRefList Inputs => HasValue ? new(_arena, _arena!.GetWorkflowCallEvent(_payload).Inputs) : default;

    public WorkflowCallEventSecretRefMap Secrets => HasValue ? new(_arena, _arena!.GetWorkflowCallEvent(_payload).Secrets) : default;

    public WorkflowCallEventOutputRefMap Outputs => HasValue ? new(_arena, _arena!.GetWorkflowCallEvent(_payload).Outputs) : default;
}

/// <summary>An input declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventInputRef
{
    private readonly AstArena? _arena;
    private readonly WorkflowCallEventInputData _row;

    internal WorkflowCallEventInputRef(AstArena? arena, in WorkflowCallEventInputData row)
    {
        _arena = arena;
        _row = row;
    }

    public bool HasValue => _arena is not null;

    public StringRef Name => new(_arena, _row.Name);

    /// <summary>The lower-cased input identifier.</summary>
    public Utf8String Id => _row.Id;

    public StringRef Description => new(_arena, _row.Description);

    public BoolRef Required => new(_arena, _row.Required);

    public StringRef Default => new(_arena, _row.Default);

    public WorkflowCallInputType Type => _row.Type;

    public TextRange Range => _row.Range;
}

/// <summary>A secret declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventSecretRef
{
    private readonly AstArena? _arena;
    private readonly WorkflowCallEventSecretData _row;

    internal WorkflowCallEventSecretRef(AstArena? arena, in WorkflowCallEventSecretData row)
    {
        _arena = arena;
        _row = row;
    }

    public StringRef Name => new(_arena, _row.Name);

    public StringRef Description => new(_arena, _row.Description);

    public BoolRef Required => new(_arena, _row.Required);

    public TextRange Range => _row.Range;
}

/// <summary>An output declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventOutputRef
{
    private readonly AstArena? _arena;
    private readonly WorkflowCallEventOutputData _row;

    internal WorkflowCallEventOutputRef(AstArena? arena, in WorkflowCallEventOutputData row)
    {
        _arena = arena;
        _row = row;
    }

    public StringRef Name => new(_arena, _row.Name);

    public StringRef Description => new(_arena, _row.Description);

    public StringRef Value => new(_arena, _row.Value);

    public TextRange Range => _row.Range;
}

/// <summary>A <c>repository_dispatch:</c> event with optional activity types.</summary>
public readonly struct RepositoryDispatchEventRef
{
    private readonly AstArena? _arena;
    private readonly int _payload;

    internal RepositoryDispatchEventRef(AstArena? arena, int payload)
    {
        _arena = arena;
        _payload = payload;
    }

    public bool HasValue => _arena is not null && _payload > 0;

    public StringRefList Types => HasValue ? new(_arena, _arena!.GetRepositoryDispatchEvent(_payload).Types) : default;
}

/// <summary>An image version event (e.g. container image update triggers).</summary>
public readonly struct ImageVersionEventRef
{
    private readonly AstArena? _arena;
    private readonly int _payload;

    internal ImageVersionEventRef(AstArena? arena, int payload)
    {
        _arena = arena;
        _payload = payload;
    }

    public bool HasValue => _arena is not null && _payload > 0;

    public StringRefList Names => HasValue ? new(_arena, _arena!.GetImageVersionEvent(_payload).Names) : default;

    public StringRefList Versions => HasValue ? new(_arena, _arena!.GetImageVersionEvent(_payload).Versions) : default;
}
