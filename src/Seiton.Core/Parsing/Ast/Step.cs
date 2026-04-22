namespace Seiton.Core.Parsing.Ast;

public sealed class Step
{
    public StringNode? Id { get; init; }

    public StringNode? If { get; init; }

    public StringNode? Name { get; init; }

    public StepExec Exec { get; init; } = null!;

    public Env? Env { get; init; }

    public BoolNode? ContinueOnError { get; init; }

    public FloatNode? TimeoutMinutes { get; init; }

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
    public StringNode Run { get; init; } = null!;

    public StringNode? Shell { get; init; }

    public StringNode? WorkingDirectory { get; init; }
}

public sealed class ExecAction : StepExec
{
    public StringNode Uses { get; init; } = null!;

    public TextRange? UsesKeyRange { get; init; }

    public SliceMap<StringNode>? Inputs { get; init; }

    public StringNode? Entrypoint { get; init; }

    public StringNode? Args { get; init; }
}
