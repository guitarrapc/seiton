namespace Seiton.Core.Flow;

// Flow DTOs are the shared machine-readable contract between the CLI
// (`check --format flow-json` / `flow-mermaid`) and the Playground flow tab.
// All values are resolved to plain strings during collection so the DTO
// outlives the ParseResult/arena that produced it.
// Contract spec: `.github/docs/Seiton_CLI_spec.md` §6.6.

/// <summary>Flow representation of a single parsed workflow document.</summary>
public sealed class WorkflowFlow
{
    public required string File { get; init; }

    public string? Name { get; init; }

    /// <summary>Trigger event names from <c>on:</c>, in document order.</summary>
    public required string[] On { get; init; }

    /// <summary>Cron entries of <c>on: schedule</c> (with the seiton <c>timezone</c> extension), empty when absent.</summary>
    public FlowSchedule[] Schedules { get; init; } = [];

    /// <summary>The workflow-level <c>concurrency:</c> declaration, or <c>null</c> when absent.</summary>
    public FlowConcurrency? Concurrency { get; init; }

    public required FlowJob[] Jobs { get; init; }
}

/// <summary>A single <c>on: schedule</c> cron entry.</summary>
public sealed class FlowSchedule
{
    public required string Cron { get; init; }

    /// <summary>IANA timezone of the seiton <c>timezone</c> extension, or <c>null</c> (UTC default).</summary>
    public string? TimeZone { get; init; }
}

/// <summary>The <c>concurrency:</c> declaration of a workflow.</summary>
public sealed class FlowConcurrency
{
    /// <summary>The concurrency group (raw expression preserved).</summary>
    public string? Group { get; init; }

    public bool CancelInProgress { get; init; }

    /// <summary>The seiton <c>queue</c> extension value, if declared.</summary>
    public string? Queue { get; init; }
}

/// <summary>Discriminates normal jobs from reusable-workflow call jobs.</summary>
public enum FlowJobKind
{
    Job,
    Reusable,
}

/// <summary>A single job node in the flow graph.</summary>
public sealed class FlowJob
{
    private string[] _needs = [];
    private string[] _reducedNeeds = [];

    public required string Id { get; init; }

    public string? Name { get; init; }

    public required FlowJobKind Kind { get; init; }

    /// <summary>The raw <c>if:</c> expression, if present.</summary>
    public string? If { get; init; }

    /// <summary>Job ids this job depends on (<c>needs:</c> edges).</summary>
    public required string[] Needs
    {
        get => _needs;
        init => _needs = value;
    }

    /// <summary>
    /// <see cref="Needs"/> after transitive reduction: edges implied by another
    /// dependency's chain are removed (e.g. <c>a</c> drops out of <c>needs: [a, b]</c>
    /// when <c>b</c> already depends on <c>a</c>). Rendering-oriented — the semantic
    /// dependency set stays in <see cref="Needs"/>.
    /// </summary>
    public required string[] ReducedNeeds
    {
        get => _reducedNeeds;
        init => _reducedNeeds = value;
    }

    /// <summary>Runner labels, or the raw whole-value expression / group name.</summary>
    public required string[] RunsOn { get; init; }

    /// <summary>The reusable workflow reference for <see cref="FlowJobKind.Reusable"/> jobs (opaque leaf).</summary>
    public string? Uses { get; init; }

    public FlowStrategy? Strategy { get; init; }

    /// <summary>The declared <c>timeout-minutes</c>, or <c>null</c> when absent or a dynamic expression.</summary>
    public double? TimeoutMinutes { get; init; }

    /// <summary>
    /// Declared permissions: <c>["read-all"]</c>/<c>["write-all"]</c> for the scalar form,
    /// <c>"scope: level"</c> entries for the mapping form (empty array = <c>{}</c> deny-all).
    /// <c>null</c> when not declared.
    /// </summary>
    public string[]? Permissions { get; init; }

    /// <summary>The deployment environment name, if declared.</summary>
    public string? Environment { get; init; }

    public required FlowStep[] Steps { get; init; }

    /// <summary>1-based start line of the job block in the source (0 when unknown).</summary>
    public int Line { get; init; }

    /// <summary>1-based end line of the job block in the source (0 when unknown).</summary>
    public int EndLine { get; init; }

    internal void SetNeeds(string[] needs)
    {
        _needs = needs;
        _reducedNeeds = needs;
    }

    internal void SetReducedNeeds(string[] reducedNeeds) => _reducedNeeds = reducedNeeds;
}

/// <summary>View of <c>strategy:</c>; static matrices are expanded into <see cref="Combinations"/>.</summary>
public sealed class FlowStrategy
{
    public required bool HasMatrix { get; init; }

    /// <summary>Matrix dimension names; empty when the matrix is a whole-value expression.</summary>
    public required string[] MatrixKeys { get; init; }

    /// <summary>Whether the matrix is a dynamic <c>${{ }}</c> expression that cannot be expanded statically.</summary>
    public required bool MatrixIsExpression { get; init; }

    /// <summary>
    /// Statically expanded combinations (cross product with <c>exclude</c> subset removal and
    /// <c>include</c> extend/append, approximating GitHub semantics). Empty when the matrix
    /// contains dynamic expressions or exceeds the 256-combination limit.
    /// </summary>
    public KeyValuePair<string, string>[][] Combinations { get; init; } = [];
}

/// <summary>How a background step is eventually joined (or not) by later steps in the same job.</summary>
public enum FlowBackgroundOutcome
{
    /// <summary>No later step waits for or cancels this background step.</summary>
    Unawaited,

    /// <summary>A later <c>wait</c> (targeting this step) or <c>wait-all</c> joins this step.</summary>
    Awaited,

    /// <summary>A later <c>cancel</c> stops this step.</summary>
    Cancelled,
}

/// <summary>The execution kind of a step node.</summary>
public enum FlowStepKind
{
    Run,
    Uses,
    Parallel,
    Wait,
    WaitAll,
    Cancel,
    Unknown,
}

/// <summary>A single step node; <see cref="Steps"/> holds children for <see cref="FlowStepKind.Parallel"/> boundaries.</summary>
public sealed class FlowStep
{
    public required FlowStepKind Kind { get; init; }

    public string? Id { get; init; }

    public string? Name { get; init; }

    /// <summary>The raw <c>if:</c> expression, if present.</summary>
    public string? If { get; init; }

    /// <summary>Whether the step runs in the background (<c>background: true</c>) — later steps do not wait for it.</summary>
    public bool Background { get; init; }

    /// <summary>
    /// How the job's later steps treat this background step: joined by a <c>wait</c>/<c>wait-all</c>,
    /// cut by a <c>cancel</c>, or never awaited. <c>null</c> for non-background steps.
    /// </summary>
    public FlowBackgroundOutcome? BackgroundOutcome { get; init; }

    /// <summary>The declared <c>timeout-minutes</c>, or <c>null</c> when absent or a dynamic expression.</summary>
    public double? TimeoutMinutes { get; init; }

    /// <summary>Whether the step declares <c>continue-on-error: true</c>.</summary>
    public bool ContinueOnError { get; init; }

    /// <summary>The script body for <see cref="FlowStepKind.Run"/> steps.</summary>
    public string? Run { get; init; }

    /// <summary>The <c>working-directory</c> of a <see cref="FlowStepKind.Run"/> step, if declared.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>The action reference for <see cref="FlowStepKind.Uses"/> steps.</summary>
    public string? Uses { get; init; }

    /// <summary>The <c>with:</c> inputs of a <see cref="FlowStepKind.Uses"/> step, in document order. <c>null</c> when absent.</summary>
    public KeyValuePair<string, string>[]? With { get; init; }

    /// <summary>Step ids awaited by a <see cref="FlowStepKind.Wait"/> step.</summary>
    public string[] WaitTargets { get; init; } = [];

    /// <summary>The step id cancelled by a <see cref="FlowStepKind.Cancel"/> step.</summary>
    public string? CancelTarget { get; init; }

    /// <summary>Nested steps of a <see cref="FlowStepKind.Parallel"/> boundary.</summary>
    public FlowStep[] Steps { get; init; } = [];

    /// <summary>1-based start line of the step in the source (0 when unknown).</summary>
    public int Line { get; init; }

    /// <summary>1-based end line of the step in the source (0 when unknown).</summary>
    public int EndLine { get; init; }
}
