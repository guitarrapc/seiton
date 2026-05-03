namespace Seiton.Core.Parsing.Ast;

/// <summary>AST node representing a GitHub Actions workflow file.</summary>
public sealed class Workflow
{
    public StringNodeId Name { get; set; }

    public StringNodeId RunName { get; set; }

    public IReadOnlyList<Event> On { get; internal set; } = [];

    public Permissions? Permissions { get; internal set; }

    public Env? Env { get; internal set; }

    public Defaults? Defaults { get; internal set; }

    public Concurrency? Concurrency { get; internal set; }

    public SliceMap<Job> Jobs { get; internal set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Name = default;
        RunName = default;
        On = [];
        Permissions = null;
        Env = null;
        Defaults = null;
        Concurrency = null;
        Jobs = default;
        Range = default;
    }
}
