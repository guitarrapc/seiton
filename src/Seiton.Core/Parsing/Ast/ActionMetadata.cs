namespace Seiton.Core.Parsing.Ast;

/// <summary>AST node representing an <c>action.yml</c> / <c>action.yaml</c> metadata file.</summary>
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

/// <summary>An input parameter declared in action metadata.</summary>
public sealed class ActionMetadataInput
{
    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public BoolNodeId Required { get; init; }

    public StringNodeId Default { get; init; }

    public StringNodeId DeprecationMessage { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>An output parameter declared in action metadata.</summary>
public sealed class ActionMetadataOutput
{
    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    public StringNodeId Value { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>The <c>runs:</c> section of action metadata defining the execution entry points.</summary>
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

    public IReadOnlyList<StringNodeId>? Args { get; init; }

    public Env? Env { get; init; }

    public IReadOnlyList<Step>? Steps { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>The <c>branding:</c> section of action metadata (icon and color).</summary>
/// <summary>The <c>branding:</c> section of action metadata (icon and color).</summary>
public sealed class ActionMetadataBranding
{
    public StringNodeId Icon { get; init; }

    public StringNodeId Color { get; init; }

    public TextRange Range { get; init; }
}
