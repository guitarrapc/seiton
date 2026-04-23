namespace Seiton.Core.Parsing.Ast;

/// <summary>AST node representing a single step within a job.</summary>
public sealed class Step
{
    public StringNodeId Id { get; init; }

    public StringNodeId If { get; init; }

    public StringNodeId Name { get; init; }

    public StepExec Exec { get; init; } = null!;

    public Env? Env { get; init; }

    public BoolNodeId ContinueOnError { get; init; }

    public FloatNodeId TimeoutMinutes { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Base class for step execution payloads (<c>run:</c> or <c>uses:</c>).</summary>
public abstract class StepExec
{
    public StepExecKind Kind { get; init; }

    public TextRange Range { get; init; }
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
    public StringNodeId Run { get; init; }

    public StringNodeId Shell { get; init; }

    public StringNodeId WorkingDirectory { get; init; }
}

/// <summary>Execution payload for a <c>uses:</c> step (action invocation).</summary>
public sealed class ExecAction : StepExec
{
    public StringNodeId Uses { get; init; }

    public TextRange? UsesKeyRange { get; init; }

    public SliceMap<StringNodeId>? Inputs { get; init; }

    public StringNodeId Entrypoint { get; init; }

    public StringNodeId Args { get; init; }
}
