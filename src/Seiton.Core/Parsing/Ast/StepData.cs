namespace Seiton.Core.Parsing.Ast;

// Data-oriented AST rows for steps and their execution payloads (Stage 2).
// A step is a tagged union: StepData.ExecKind selects the payload table and
// StepData.ExecPayload is the 1-based index into that table (0 = no payload),
// mirroring the EventData Kind + Payload pattern.
// Step lists (Job.Steps, ExecParallelData.Steps, ActionMetadataRuns.Steps) are
// StepIdRange values over the arena's shared StepId list store — nested parallel
// parsing appends step rows non-contiguously, so lists never range over the row
// table directly.

/// <summary>Row data for a single step within a job (or composite action).</summary>
public readonly struct StepData
{
    public StringNodeId Id { get; init; }

    public StringNodeId If { get; init; }

    public TextRange? IfKeyRange { get; init; }

    public StringNodeId Name { get; init; }

    /// <summary>Background modifier on <c>run</c> / <c>uses</c> steps only.</summary>
    public BoolNodeId Background { get; init; }

    /// <summary>Discriminator selecting the exec payload table.</summary>
    public StepExecKind ExecKind { get; init; }

    /// <summary>1-based index into the payload table selected by <see cref="ExecKind"/> (0 = none).</summary>
    public int ExecPayload { get; init; }

    public EnvId Env { get; init; }

    public BoolNodeId ContinueOnError { get; init; }

    public FloatNodeId TimeoutMinutes { get; init; }

    public TextRange Range { get; init; }
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

/// <summary>Payload row for a <c>run:</c> step.</summary>
public readonly struct ExecRunData
{
    public StringNodeId Run { get; init; }

    public StringNodeId Shell { get; init; }

    public StringNodeId WorkingDirectory { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Payload row for a <c>uses:</c> step (action invocation).</summary>
public readonly struct ExecActionData
{
    public StringNodeId Uses { get; init; }

    public TextRange? UsesKeyRange { get; init; }

    /// <summary>The <c>with:</c> inputs — range over <see cref="ActionInputData"/> rows. default = no with: block.</summary>
    public NodeRange Inputs { get; init; }

    public StringNodeId Entrypoint { get; init; }

    public StringNodeId Args { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>One <c>with:</c> input entry (key embedded in the row; lookup is case-insensitive).</summary>
public readonly struct ActionInputData
{
    public Utf8Slice Key { get; init; }

    public StringNodeId Value { get; init; }
}

/// <summary>Payload row for a <c>wait:</c> step.</summary>
public readonly struct ExecWaitData
{
    public StringIdRange Targets { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Payload row for a <c>wait-all:</c> step.</summary>
public readonly struct ExecWaitAllData
{
    public TextRange Range { get; init; }
}

/// <summary>Payload row for a <c>cancel:</c> step.</summary>
public readonly struct ExecCancelData
{
    public StringNodeId Target { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Payload row for a <c>parallel:</c> step.</summary>
public readonly struct ExecParallelData
{
    /// <summary>Nested steps — range over the shared <see cref="StepId"/> list store.</summary>
    public StepIdRange Steps { get; init; }

    public TextRange Range { get; init; }
}
