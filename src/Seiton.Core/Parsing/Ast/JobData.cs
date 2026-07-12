namespace Seiton.Core.Parsing.Ast;

// Data-oriented AST rows for jobs (Stage 3).
// The workflow jobs map is a NodeRange over JobEntryData rows (key + JobId), NOT over
// the JobData row table: map entries and job rows are independently addressable.

/// <summary>Row data for a single job in a workflow.</summary>
public readonly struct JobData
{
    public StringNodeId Id { get; init; }

    public StringNodeId Name { get; init; }

    public StringIdRange Needs { get; init; }

    public RunnerId RunsOn { get; init; }

    public TextRange? RunsOnKeyRange { get; init; }

    public PermissionsId Permissions { get; init; }

    public EnvironmentId Environment { get; init; }

    public ConcurrencyId Concurrency { get; init; }

    /// <summary>The <c>outputs:</c> map — range over <see cref="JobOutputData"/> rows. default = key absent.</summary>
    public NodeRange Outputs { get; init; }

    public EnvId Env { get; init; }

    public DefaultsId Defaults { get; init; }

    public StringNodeId If { get; init; }

    public TextRange? IfKeyRange { get; init; }

    public StepIdRange Steps { get; init; }

    public TextRange? StepsKeyRange { get; init; }

    public FloatNodeId TimeoutMinutes { get; init; }

    public StrategyId Strategy { get; init; }

    public BoolNodeId ContinueOnError { get; init; }

    public ContainerId Container { get; init; }

    public ServicesId Services { get; init; }

    public WorkflowCallId WorkflowCall { get; init; }

    public SnapshotId Snapshot { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>One workflow jobs-map entry (key embedded; lookup is case-insensitive).</summary>
public readonly struct JobEntryData
{
    public Utf8Slice Key { get; init; }

    public JobId Job { get; init; }
}

/// <summary>One job <c>outputs:</c> entry (key embedded; lookup is case-insensitive).</summary>
public readonly struct JobOutputData
{
    public Utf8Slice Key { get; init; }

    public StringNodeId Value { get; init; }
}
