namespace Seiton.Core.Parsing.Ast;

public abstract class Event
{
    public StringNodeId EventName { get; init; }

    public TextRange Range { get; init; }
}

public sealed class WebhookEvent : Event
{
    public StringNodeId Hook { get; init; }

    public StringNodeId[]? Types { get; init; }

    public WebhookEventFilter? Branches { get; init; }

    public WebhookEventFilter? BranchesIgnore { get; init; }

    public WebhookEventFilter? Tags { get; init; }

    public WebhookEventFilter? TagsIgnore { get; init; }

    public WebhookEventFilter? Paths { get; init; }

    public WebhookEventFilter? PathsIgnore { get; init; }

    public StringNodeId[]? Workflows { get; init; }
}

public sealed class WebhookEventFilter
{
    public StringNodeId Name { get; init; }

    public StringNodeId[] Values { get; init; } = [];
}

public sealed class ScheduledEvent : Event
{
    public IReadOnlyList<ScheduleEntry> Schedules { get; init; } = [];
}

public sealed class ScheduleEntry
{
    public StringNodeId Cron { get; init; }

    public StringNodeId Timezone { get; init; }

    public TextRange Range { get; init; }
}

public sealed class WorkflowDispatchEvent : Event
{
    public SliceMap<DispatchInput>? Inputs { get; init; }
}

public sealed class DispatchInput
{
    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public BoolNodeId Required { get; init; }

    public StringNodeId Default { get; init; }

    public DispatchInputType Type { get; init; }

    public StringNodeId[]? Options { get; init; }

    public TextRange Range { get; init; }
}

public enum DispatchInputType
{
    None,
    String,
    Number,
    Boolean,
    Choice,
    Environment,
}

public sealed class WorkflowCallEvent : Event
{
    public IReadOnlyList<WorkflowCallEventInput>? Inputs { get; init; }

    public SliceMap<WorkflowCallEventSecret>? Secrets { get; init; }

    public SliceMap<WorkflowCallEventOutput>? Outputs { get; init; }
}

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

public enum WorkflowCallInputType
{
    Invalid,
    Boolean,
    Number,
    String,
}

public sealed class WorkflowCallEventSecret
{
    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public BoolNodeId Required { get; init; }

    public TextRange Range { get; init; }
}

public sealed class WorkflowCallEventOutput
{
    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public StringNodeId Value { get; init; }

    public TextRange Range { get; init; }
}

public sealed class RepositoryDispatchEvent : Event
{
    public StringNodeId[]? Types { get; init; }
}

public sealed class ImageVersionEvent : Event
{
    public StringNodeId[]? Names { get; init; }

    public StringNodeId[]? Versions { get; init; }
}
