namespace Seiton.Core.Flow;

// Flow DTOs are the shared machine-readable contract between the CLI
// (`check --format flow-json`) and the Playground flow tab. All values are
// resolved to plain strings during collection so the DTO outlives the
// ParseResult/arena that produced it. See `.github/docs/plans/plan_flow.md`.

/// <summary>Flow representation of a single parsed workflow document.</summary>
public sealed class WorkflowFlow
{
    public required string File { get; init; }

    public string? Name { get; init; }

    /// <summary>Trigger event names from <c>on:</c>, in document order.</summary>
    public required string[] On { get; init; }

    public required FlowJob[] Jobs { get; init; }
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
    public required string Id { get; init; }

    public string? Name { get; init; }

    public required FlowJobKind Kind { get; init; }

    /// <summary>The raw <c>if:</c> expression, if present.</summary>
    public string? If { get; init; }

    /// <summary>Job ids this job depends on (<c>needs:</c> edges).</summary>
    public required string[] Needs { get; init; }

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

    /// <summary>The declared <c>timeout-minutes</c>, or <c>null</c> when absent or a dynamic expression.</summary>
    public double? TimeoutMinutes { get; init; }

    /// <summary>Whether the step declares <c>continue-on-error: true</c>.</summary>
    public bool ContinueOnError { get; init; }

    /// <summary>The script body for <see cref="FlowStepKind.Run"/> steps.</summary>
    public string? Run { get; init; }

    /// <summary>The action reference for <see cref="FlowStepKind.Uses"/> steps.</summary>
    public string? Uses { get; init; }

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
