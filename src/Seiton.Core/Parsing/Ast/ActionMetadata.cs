namespace Seiton.Core.Parsing.Ast;

public sealed class ActionMetadata
{
    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public SliceMap<ActionMetadataInput>? Inputs { get; init; }

    public SliceMap<ActionMetadataOutput>? Outputs { get; init; }

    public ActionMetadataRuns? Runs { get; init; }

    public ActionMetadataBranding? Branding { get; init; }

    public TextRange Range { get; init; }
}

public sealed class ActionMetadataInput
{
    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public BoolNodeId Required { get; init; }

    public StringNodeId Default { get; init; }

    public StringNodeId DeprecationMessage { get; init; }

    public TextRange Range { get; init; }
}

public sealed class ActionMetadataOutput
{
    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public StringNodeId Value { get; init; }

    public TextRange Range { get; init; }
}

public sealed class ActionMetadataRuns
{
    public StringNodeId Using { get; init; }

    public StringNodeId Main { get; init; }

    public StringNodeId Pre { get; init; }

    public StringNodeId Post { get; init; }

    public StringNodeId PreIf { get; init; }

    public StringNodeId PostIf { get; init; }

    public StringNodeId Image { get; init; }

    public StringNodeId Entrypoint { get; init; }

    public StringNodeId[]? Args { get; init; }

    public Env? Env { get; init; }

    public IReadOnlyList<Step>? Steps { get; init; }

    public TextRange Range { get; init; }
}

public sealed class ActionMetadataBranding
{
    public StringNodeId Icon { get; init; }

    public StringNodeId Color { get; init; }

    public TextRange Range { get; init; }
}
