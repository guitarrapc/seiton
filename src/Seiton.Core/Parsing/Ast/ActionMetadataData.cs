namespace Seiton.Core.Parsing.Ast;

// Data-oriented AST rows for the action.yml metadata family (Stage 2).
// Inputs/Outputs are key-embedded row maps (NodeRange over contiguous rows,
// case-INSENSITIVE lookup); Runs/Branding are single rows addressed by typed IDs.
// The ActionMetadata root class remains until Stage 3 (same as Workflow).

/// <summary>Row data for one input parameter declared in action metadata (key embedded; lookup is case-insensitive).</summary>
public readonly struct ActionMetadataInputData
{
    public Utf8Slice Key { get; init; }

    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public BoolNodeId Required { get; init; }

    public StringNodeId Default { get; init; }

    public StringNodeId DeprecationMessage { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for one output parameter declared in action metadata (key embedded; lookup is case-insensitive).</summary>
public readonly struct ActionMetadataOutputData
{
    public Utf8Slice Key { get; init; }

    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public StringNodeId Value { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for the <c>runs:</c> section of action metadata.</summary>
public readonly struct ActionMetadataRunsData
{
    public StringNodeId Using { get; init; }

    public StringNodeId Main { get; init; }

    public StringNodeId Pre { get; init; }

    public StringNodeId Post { get; init; }

    public StringNodeId PreIf { get; init; }

    public StringNodeId PostIf { get; init; }

    public StringNodeId Image { get; init; }

    public StringNodeId Entrypoint { get; init; }

    public StringIdRange Args { get; init; }

    public EnvId Env { get; init; }

    /// <summary>Composite action steps — range over the shared <see cref="StepId"/> list store.</summary>
    public StepIdRange Steps { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for the <c>branding:</c> section of action metadata.</summary>
public readonly struct ActionMetadataBrandingData
{
    public StringNodeId Icon { get; init; }

    public StringNodeId Color { get; init; }

    public TextRange Range { get; init; }
}
