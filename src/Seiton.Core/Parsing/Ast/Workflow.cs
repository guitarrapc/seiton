namespace Seiton.Core.Parsing.Ast;

public sealed class Workflow
{
    public StringNode? Name { get; init; }

    public StringNode? RunName { get; init; }

    public IReadOnlyList<Event> On { get; init; } = [];

    public Permissions? Permissions { get; init; }

    public Env? Env { get; init; }

    public Defaults? Defaults { get; init; }

    public Concurrency? Concurrency { get; init; }

    public IReadOnlyDictionary<Utf8String, Job> Jobs { get; init; } = new Dictionary<Utf8String, Job>();

    public TextRange Range { get; init; }
}
