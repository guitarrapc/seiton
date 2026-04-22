namespace Seiton.Core.Parsing.Ast;

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

public abstract class StepExec
{
    public StepExecKind Kind { get; init; }

    public TextRange Range { get; init; }
}

public enum StepExecKind
{
    Run,
    Action,
}

public sealed class ExecRun : StepExec
{
    public StringNodeId Run { get; init; }

    public StringNodeId Shell { get; init; }

    public StringNodeId WorkingDirectory { get; init; }
}

public sealed class ExecAction : StepExec
{
    public StringNodeId Uses { get; init; }

    public TextRange? UsesKeyRange { get; init; }

    public SliceMap<StringNodeId>? Inputs { get; init; }

    public StringNodeId Entrypoint { get; init; }

    public StringNodeId Args { get; init; }
}
