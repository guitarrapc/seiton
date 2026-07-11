namespace Seiton.Core.Parsing.Ast;

/// <summary>AST node representing a single job in a workflow.</summary>
public sealed class Job
{
    public StringNodeId Id { get; set; }

    public StringNodeId Name { get; set; }

    public StringIdRange Needs { get; set; }

    public RunnerId RunsOn { get; set; }

    public TextRange? RunsOnKeyRange { get; set; }

    public PermissionsId Permissions { get; set; }

    public EnvironmentId Environment { get; set; }

    public ConcurrencyId Concurrency { get; set; }

    public SliceMap<StringNodeId>? Outputs { get; set; }

    public EnvId Env { get; set; }

    public DefaultsId Defaults { get; set; }

    public StringNodeId If { get; set; }

    public TextRange? IfKeyRange { get; set; }

    public StepIdRange Steps { get; set; }

    public TextRange? StepsKeyRange { get; set; }

    public FloatNodeId TimeoutMinutes { get; set; }

    public StrategyId Strategy { get; set; }

    public BoolNodeId ContinueOnError { get; set; }

    public ContainerId Container { get; set; }

    public ServicesId Services { get; set; }

    public WorkflowCallId WorkflowCall { get; set; }

    public SnapshotId Snapshot { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Id = default;
        Name = default;
        Needs = default;
        RunsOn = default;
        RunsOnKeyRange = null;
        Permissions = default;
        Environment = default;
        Concurrency = default;
        Outputs = null;
        Env = default;
        Defaults = default;
        If = default;
        IfKeyRange = null;
        Steps = default;
        StepsKeyRange = null;
        TimeoutMinutes = default;
        Strategy = default;
        ContinueOnError = default;
        Container = default;
        Services = default;
        WorkflowCall = default;
        Snapshot = default;
        Range = default;
    }
}
