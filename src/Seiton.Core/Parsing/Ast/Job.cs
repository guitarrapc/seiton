namespace Seiton.Core.Parsing.Ast;

/// <summary>AST node representing a snapshot configuration for a job.</summary>
public sealed class Snapshot
{
    public StringNodeId Version { get; set; }

    public StringNodeId ImageName { get; set; }

    public StringNodeId If { get; set; }

    public TextRange? IfKeyRange { get; set; }

    internal void Reset()
    {
        Version = default;
        ImageName = default;
        If = default;
        IfKeyRange = null;
    }
}

/// <summary>AST node representing a single job in a workflow.</summary>
public sealed class Job
{
    public StringNodeId Id { get; set; }

    public StringNodeId Name { get; set; }

    public IReadOnlyList<StringNodeId>? Needs { get; set; }

    public Runner? RunsOn { get; set; }

    public TextRange? RunsOnKeyRange { get; set; }

    public Permissions? Permissions { get; set; }

    public Environment? Environment { get; set; }

    public Concurrency? Concurrency { get; set; }

    public SliceMap<StringNodeId>? Outputs { get; set; }

    public Env? Env { get; set; }

    public Defaults? Defaults { get; set; }

    public StringNodeId If { get; set; }

    public TextRange? IfKeyRange { get; set; }

    public IReadOnlyList<Step>? Steps { get; set; }

    public TextRange? StepsKeyRange { get; set; }

    public FloatNodeId TimeoutMinutes { get; set; }

    public Strategy? Strategy { get; set; }

    public BoolNodeId ContinueOnError { get; set; }

    public Container? Container { get; set; }

    public Services? Services { get; set; }

    public WorkflowCall? WorkflowCall { get; set; }

    public Snapshot? Snapshot { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Id = default;
        Name = default;
        Needs = null;
        RunsOn = null;
        RunsOnKeyRange = null;
        Permissions = null;
        Environment = null;
        Concurrency = null;
        Outputs = null;
        Env = null;
        Defaults = null;
        If = default;
        IfKeyRange = null;
        Steps = null;
        StepsKeyRange = null;
        TimeoutMinutes = default;
        Strategy = null;
        ContinueOnError = default;
        Container = null;
        Services = null;
        WorkflowCall = null;
        Snapshot = null;
        Range = default;
    }
}
