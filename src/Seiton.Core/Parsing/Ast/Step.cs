namespace Seiton.Core.Parsing.Ast;

/// <summary>AST node representing a single step within a job.</summary>
public sealed class Step
{
    public StringNodeId Id { get; set; }

    public StringNodeId If { get; set; }

    public TextRange? IfKeyRange { get; set; }

    public StringNodeId Name { get; set; }

    /// <summary>Background modifier on <c>run</c> / <c>uses</c> steps only.</summary>
    public BoolNodeId Background { get; set; }

    public StepExec Exec { get; set; } = null!;

    public Env? Env { get; set; }

    public BoolNodeId ContinueOnError { get; set; }

    public FloatNodeId TimeoutMinutes { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Id = default;
        If = default;
        IfKeyRange = null;
        Name = default;
        Background = default;
        Exec = null!;
        Env = null;
        ContinueOnError = default;
        TimeoutMinutes = default;
        Range = default;
    }
}

/// <summary>Base class for step execution payloads.</summary>
public abstract class StepExec
{
    public StepExecKind Kind { get; set; }

    public TextRange Range { get; set; }
}

/// <summary>Discriminator for step execution kind. <see cref="None"/> represents an absent payload (default ref).</summary>
public enum StepExecKind
{
    None,
    Run,
    Action,
    Wait,
    WaitAll,
    Cancel,
    Parallel,
}

/// <summary>Execution payload for a <c>run:</c> step.</summary>
public sealed class ExecRun : StepExec
{
    public StringNodeId Run { get; set; }

    public StringNodeId Shell { get; set; }

    public StringNodeId WorkingDirectory { get; set; }

    internal void Reset()
    {
        Kind = StepExecKind.Run;
        Run = default;
        Shell = default;
        WorkingDirectory = default;
        Range = default;
    }
}

/// <summary>Execution payload for a <c>uses:</c> step (action invocation).</summary>
public sealed class ExecAction : StepExec
{
    public StringNodeId Uses { get; set; }

    public TextRange? UsesKeyRange { get; set; }

    public SliceMap<StringNodeId>? Inputs { get; set; }

    public StringNodeId Entrypoint { get; set; }

    public StringNodeId Args { get; set; }

    internal void Reset()
    {
        Kind = StepExecKind.Action;
        Uses = default;
        UsesKeyRange = null;
        Inputs = null;
        Entrypoint = default;
        Args = default;
        Range = default;
    }
}

/// <summary>Execution payload for a <c>wait:</c> step.</summary>
public sealed class ExecWait : StepExec
{
    public IReadOnlyList<StringNodeId>? Targets { get; set; }

    internal void Reset()
    {
        Kind = StepExecKind.Wait;
        Targets = null;
        Range = default;
    }
}

/// <summary>Execution payload for a <c>wait-all:</c> step.</summary>
public sealed class ExecWaitAll : StepExec
{
    internal void Reset()
    {
        Kind = StepExecKind.WaitAll;
        Range = default;
    }
}

/// <summary>Execution payload for a <c>cancel:</c> step.</summary>
public sealed class ExecCancel : StepExec
{
    public StringNodeId Target { get; set; }

    internal void Reset()
    {
        Kind = StepExecKind.Cancel;
        Target = default;
        Range = default;
    }
}

/// <summary>Execution payload for a <c>parallel:</c> step.</summary>
public sealed class ExecParallel : StepExec
{
    public IReadOnlyList<Step>? Steps { get; set; }

    internal void Reset()
    {
        Kind = StepExecKind.Parallel;
        Steps = null;
        Range = default;
    }
}
