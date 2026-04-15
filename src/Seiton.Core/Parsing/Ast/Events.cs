namespace Seiton.Core.Parsing.Ast;

public abstract class Event
{
    public StringNode EventName { get; init; } = null!;

    public TextRange Range { get; init; }
}

public sealed class WebhookEvent : Event
{
    public StringNode Hook { get; init; } = null!;

    public IReadOnlyList<StringNode>? Types { get; init; }

    public WebhookEventFilter? Branches { get; init; }

    public WebhookEventFilter? BranchesIgnore { get; init; }

    public WebhookEventFilter? Tags { get; init; }

    public WebhookEventFilter? TagsIgnore { get; init; }

    public WebhookEventFilter? Paths { get; init; }

    public WebhookEventFilter? PathsIgnore { get; init; }

    public IReadOnlyList<StringNode>? Workflows { get; init; }
}

public sealed class WebhookEventFilter
{
    public StringNode Name { get; init; } = null!;

    public IReadOnlyList<StringNode> Values { get; init; } = [];
}

public sealed class ScheduledEvent : Event
{
    public IReadOnlyList<ScheduleEntry> Schedules { get; init; } = [];
}

public sealed class ScheduleEntry
{
    public StringNode? Cron { get; init; }

    public StringNode? Timezone { get; init; }

    public TextRange Range { get; init; }
}

public sealed class WorkflowDispatchEvent : Event
{
    public IReadOnlyDictionary<Utf8String, DispatchInput>? Inputs { get; init; }
}

public sealed class DispatchInput
{
    public StringNode Name { get; init; } = null!;

    public StringNode? Description { get; init; }

    public BoolNode? Required { get; init; }

    public StringNode? Default { get; init; }

    public DispatchInputType Type { get; init; }

    public IReadOnlyList<StringNode>? Options { get; init; }

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

    public IReadOnlyDictionary<Utf8String, WorkflowCallEventSecret>? Secrets { get; init; }

    public IReadOnlyDictionary<Utf8String, WorkflowCallEventOutput>? Outputs { get; init; }
}

public sealed class WorkflowCallEventInput
{
    public StringNode Name { get; init; } = null!;

    public Utf8String Id { get; init; }

    public StringNode? Description { get; init; }

    public BoolNode? Required { get; init; }

    public StringNode? Default { get; init; }

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
    public StringNode Name { get; init; } = null!;

    public StringNode? Description { get; init; }

    public BoolNode? Required { get; init; }

    public TextRange Range { get; init; }
}

public sealed class WorkflowCallEventOutput
{
    public StringNode Name { get; init; } = null!;

    public StringNode? Description { get; init; }

    public StringNode? Value { get; init; }

    public TextRange Range { get; init; }
}

public sealed class RepositoryDispatchEvent : Event
{
    public IReadOnlyList<StringNode>? Types { get; init; }
}

public sealed class ImageVersionEvent : Event
{
    public IReadOnlyList<StringNode>? Names { get; init; }

    public IReadOnlyList<StringNode>? Versions { get; init; }
}
