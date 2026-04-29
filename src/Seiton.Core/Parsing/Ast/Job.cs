namespace Seiton.Core.Parsing.Ast;

/// <summary>AST node representing a snapshot configuration for a job.</summary>
public sealed class Snapshot
{
    public StringNodeId Version { get; init; }

    public StringNodeId ImageName { get; init; }

    public StringNodeId If { get; init; }

    public TextRange? IfKeyRange { get; init; }
}

/// <summary>AST node representing a single job in a workflow.</summary>
public sealed class Job
{
    public StringNodeId Id { get; init; }

    public StringNodeId Name { get; init; }

    public StringNodeId[]? Needs { get; init; }

    public Runner? RunsOn { get; init; }

    public TextRange? RunsOnKeyRange { get; init; }

    public Permissions? Permissions { get; init; }

    public Environment? Environment { get; init; }

    public Concurrency? Concurrency { get; init; }

    public SliceMap<StringNodeId>? Outputs { get; init; }

    public Env? Env { get; init; }

    public Defaults? Defaults { get; init; }

    public StringNodeId If { get; init; }

    public TextRange? IfKeyRange { get; init; }

    public IReadOnlyList<Step>? Steps { get; init; }

    public TextRange? StepsKeyRange { get; init; }

    public FloatNodeId TimeoutMinutes { get; init; }

    public Strategy? Strategy { get; init; }

    public BoolNodeId ContinueOnError { get; init; }

    public Container? Container { get; init; }

    public Services? Services { get; init; }

    public WorkflowCall? WorkflowCall { get; init; }

    public Snapshot? Snapshot { get; init; }

    public TextRange Range { get; init; }
}
