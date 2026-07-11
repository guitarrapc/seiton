namespace Seiton.Core.Parsing.Ast;

/// <summary>Base class for all trigger event types in the <c>on:</c> section.</summary>
public abstract class Event
{
    public StringNodeId EventName { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>A webhook-triggered event (e.g. <c>push</c>, <c>pull_request</c>).</summary>
public sealed class WebhookEvent : Event
{
    public StringNodeId Hook { get; init; }

    public StringIdRange Types { get; init; }

    public WebhookEventFilter? Branches { get; init; }

    public WebhookEventFilter? BranchesIgnore { get; init; }

    public WebhookEventFilter? Tags { get; init; }

    public WebhookEventFilter? TagsIgnore { get; init; }

    public WebhookEventFilter? Paths { get; init; }

    public WebhookEventFilter? PathsIgnore { get; init; }

    public StringIdRange Workflows { get; init; }
}

/// <summary>A branch/path/tag filter within a webhook event.</summary>
public sealed class WebhookEventFilter
{
    public StringNodeId Name { get; init; }

    public StringIdRange Values { get; init; }
}

/// <summary>A <c>schedule:</c> event containing cron entries.</summary>
public sealed class ScheduledEvent : Event
{
    public IReadOnlyList<ScheduleEntry> Schedules { get; init; } = [];
}

/// <summary>A single cron schedule entry.</summary>
public readonly struct ScheduleEntry
{
    public StringNodeId Cron { get; init; }

    public StringNodeId Timezone { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>A <c>workflow_dispatch:</c> event with optional inputs.</summary>
public sealed class WorkflowDispatchEvent : Event
{
    public SliceMap<DispatchInput>? Inputs { get; init; }
}

/// <summary>An input parameter for a <c>workflow_dispatch</c> event.</summary>
public sealed class DispatchInput
{
    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public BoolNodeId Required { get; init; }

    public StringNodeId Default { get; init; }

    public DispatchInputType Type { get; init; }

    public StringIdRange Options { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Type discriminator for <c>workflow_dispatch</c> input parameters.</summary>
public enum DispatchInputType
{
    None,
    String,
    Number,
    Boolean,
    Choice,
    Environment,
}

/// <summary>A <c>workflow_call:</c> event defining reusable workflow inputs/outputs/secrets.</summary>
public sealed class WorkflowCallEvent : Event
{
    public IReadOnlyList<WorkflowCallEventInput>? Inputs { get; init; }

    public SliceMap<WorkflowCallEventSecret>? Secrets { get; init; }

    public SliceMap<WorkflowCallEventOutput>? Outputs { get; init; }
}

/// <summary>An input declared on a <c>workflow_call</c> event.</summary>
public sealed class WorkflowCallEventInput
{
    public StringNodeId Name { get; init; }

    public Utf8String Id { get; init; }

    public StringNodeId Description { get; init; }

    public BoolNodeId Required { get; init; }

    public StringNodeId Default { get; init; }

    public WorkflowCallInputType Type { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Type discriminator for <c>workflow_call</c> input parameters.</summary>
public enum WorkflowCallInputType
{
    Invalid,
    Boolean,
    Number,
    String,
}

/// <summary>A secret declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventSecret
{
    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public BoolNodeId Required { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>An output declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventOutput
{
    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public StringNodeId Value { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>A <c>repository_dispatch:</c> event with optional activity types.</summary>
public sealed class RepositoryDispatchEvent : Event
{
    public StringIdRange Types { get; init; }
}

/// <summary>An image version event (e.g. container image update triggers).</summary>
public sealed class ImageVersionEvent : Event
{
    public StringIdRange Names { get; init; }

    public StringIdRange Versions { get; init; }
}
