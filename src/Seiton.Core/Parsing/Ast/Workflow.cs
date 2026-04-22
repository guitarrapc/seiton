namespace Seiton.Core.Parsing.Ast;

public sealed class Workflow
{
    public StringNodeId Name { get; init; }

    public StringNodeId RunName { get; init; }

    public IReadOnlyList<Event> On { get; init; } = [];

    public Permissions? Permissions { get; init; }

    public Env? Env { get; init; }

    public Defaults? Defaults { get; init; }

    public Concurrency? Concurrency { get; init; }

    public SliceMap<Job> Jobs { get; init; }

    public TextRange Range { get; init; }
}
