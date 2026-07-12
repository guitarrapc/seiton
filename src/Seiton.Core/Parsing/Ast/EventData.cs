using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing.Ast;

// Data-oriented rows for the `on:` event family (Stage 2). Each event is a tagged-union
// header row (EventData) whose Payload indexes a kind-specific payload table.
// Payload rows are appended BEFORE their header row; header rows for one `on:` section
// are contiguous, so Workflow.On is a NodeRange over the event table.

/// <summary>Handle referencing a <see cref="WebhookEventFilterData"/> row. <c>default</c> = filter absent.</summary>
public readonly record struct WebhookFilterId
{
    private readonly int _raw;

    internal WebhookFilterId(int raw) => _raw = raw;

    public bool HasValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _raw > 0;
    }

    internal int Index
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _raw - 1;
    }
}

/// <summary>Tagged-union header row for a trigger event in the <c>on:</c> section.</summary>
public readonly struct EventData
{
    public EventKind Kind { get; init; }

    public StringNodeId EventName { get; init; }

    public TextRange Range { get; init; }

    /// <summary>1-based index into the payload table selected by <see cref="Kind"/> (0 = no payload).</summary>
    public int Payload { get; init; }
}

/// <summary>Payload row for a webhook-triggered event (e.g. <c>push</c>, <c>pull_request</c>).</summary>
public readonly struct WebhookEventData
{
    public StringNodeId Hook { get; init; }

    public StringIdRange Types { get; init; }

    public WebhookFilterId Branches { get; init; }

    public WebhookFilterId BranchesIgnore { get; init; }

    public WebhookFilterId Tags { get; init; }

    public WebhookFilterId TagsIgnore { get; init; }

    public WebhookFilterId Paths { get; init; }

    public WebhookFilterId PathsIgnore { get; init; }

    public StringIdRange Workflows { get; init; }
}

/// <summary>Row for a branch/path/tag filter within a webhook event.</summary>
public readonly struct WebhookEventFilterData
{
    public StringNodeId Name { get; init; }

    public StringIdRange Values { get; init; }
}

/// <summary>Payload row for a <c>schedule:</c> event.</summary>
public readonly struct ScheduledEventData
{
    /// <summary>Range over the schedule-entry row table.</summary>
    public NodeRange Schedules { get; init; }
}

/// <summary>A single cron schedule entry.</summary>
public readonly struct ScheduleEntry
{
    public StringNodeId Cron { get; init; }

    public StringNodeId Timezone { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Payload row for a <c>workflow_dispatch:</c> event.</summary>
public readonly struct WorkflowDispatchEventData
{
    /// <summary>Range over the dispatch-input row table. Key lookup is case-insensitive.</summary>
    public NodeRange Inputs { get; init; }
}

/// <summary>Row for an input parameter of a <c>workflow_dispatch</c> event. Key lookup is case-insensitive.</summary>
public readonly struct DispatchInputData
{
    /// <summary>The raw map key slice used for lookup.</summary>
    public Utf8Slice Key { get; init; }

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

/// <summary>Payload row for a <c>workflow_call:</c> event.</summary>
public readonly struct WorkflowCallEventData
{
    /// <summary>Range over the workflow-call event input row table (document-order list).</summary>
    public NodeRange Inputs { get; init; }

    /// <summary>Range over the workflow-call event secret row table. Key lookup is case-insensitive.</summary>
    public NodeRange Secrets { get; init; }

    /// <summary>Range over the workflow-call event output row table. Key lookup is case-insensitive.</summary>
    public NodeRange Outputs { get; init; }
}

/// <summary>Row for an input declared on a <c>workflow_call</c> event.</summary>
public readonly struct WorkflowCallEventInputData
{
    public StringNodeId Name { get; init; }

    /// <summary>The lower-cased input identifier.</summary>
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

/// <summary>Row for a secret declared on a <c>workflow_call</c> event. Key lookup is case-insensitive.</summary>
public readonly struct WorkflowCallEventSecretData
{
    /// <summary>The raw map key slice used for lookup.</summary>
    public Utf8Slice Key { get; init; }

    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public BoolNodeId Required { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row for an output declared on a <c>workflow_call</c> event. Key lookup is case-insensitive.</summary>
public readonly struct WorkflowCallEventOutputData
{
    /// <summary>The raw map key slice used for lookup.</summary>
    public Utf8Slice Key { get; init; }

    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public StringNodeId Value { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Payload row for a <c>repository_dispatch:</c> event.</summary>
public readonly struct RepositoryDispatchEventData
{
    public StringIdRange Types { get; init; }
}

/// <summary>Payload row for an image version event (e.g. container image update triggers).</summary>
public readonly struct ImageVersionEventData
{
    public StringIdRange Names { get; init; }

    public StringIdRange Versions { get; init; }
}
