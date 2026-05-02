namespace Seiton.Core.Parsing.Ast;

/// <summary>AST node representing a GitHub Actions workflow file.</summary>
public sealed class Workflow
{
    public StringNodeId Name { get; init; }

    public StringNodeId RunName { get; init; }

    public IReadOnlyList<Event> On { get; internal set; } = [];

    public Permissions? Permissions { get; internal set; }

    public Env? Env { get; internal set; }

    public Defaults? Defaults { get; internal set; }

    public Concurrency? Concurrency { get; internal set; }

    public SliceMap<Job> Jobs { get; init; }

    public TextRange Range { get; init; }
}
