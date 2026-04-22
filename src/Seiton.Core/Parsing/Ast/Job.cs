namespace Seiton.Core.Parsing.Ast;

public sealed class Job
{
    public StringNode Id { get; init; } = null!;

    public StringNode? Name { get; init; }

    public IReadOnlyList<StringNode>? Needs { get; init; }

    public Runner? RunsOn { get; init; }

    public Permissions? Permissions { get; init; }

    public Environment? Environment { get; init; }

    public Concurrency? Concurrency { get; init; }

    public SliceMap<StringNode>? Outputs { get; init; }

    public Env? Env { get; init; }

    public Defaults? Defaults { get; init; }

    public StringNode? If { get; init; }

    public IReadOnlyList<Step>? Steps { get; init; }

    public FloatNode? TimeoutMinutes { get; init; }

    public Strategy? Strategy { get; init; }

    public BoolNode? ContinueOnError { get; init; }

    public Container? Container { get; init; }

    public Services? Services { get; init; }

    public WorkflowCall? WorkflowCall { get; init; }

    public TextRange Range { get; init; }
}
