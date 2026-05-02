namespace Seiton.Core.Parsing.Ast;

/// <summary>AST node representing a single step within a job.</summary>
public sealed class Step
{
    public StringNodeId Id { get; set; }

    public StringNodeId If { get; set; }

    public TextRange? IfKeyRange { get; set; }

    public StringNodeId Name { get; set; }

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
        Exec = null!;
        Env = null;
        ContinueOnError = default;
        TimeoutMinutes = default;
        Range = default;
    }
}

/// <summary>Base class for step execution payloads (<c>run:</c> or <c>uses:</c>).</summary>
public abstract class StepExec
{
    public StepExecKind Kind { get; set; }

    public TextRange Range { get; set; }
}

/// <summary>Discriminator for step execution kind.</summary>
public enum StepExecKind
{
    Run,
    Action,
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
