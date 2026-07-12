namespace Seiton.Core.Parsing.Ast;

/// <summary>AST node representing an <c>action.yml</c> / <c>action.yaml</c> metadata file.</summary>
public sealed class ActionMetadata
{
    public StringNodeId Name { get; init; }

    public StringNodeId Description { get; init; }

    /// <summary>Range over <see cref="ActionMetadataInputData"/> rows. <c>default</c> = key absent.</summary>
    public NodeRange Inputs { get; init; }

    /// <summary>Range over <see cref="ActionMetadataOutputData"/> rows. <c>default</c> = key absent.</summary>
    public NodeRange Outputs { get; init; }

    public ActionMetadataRunsId Runs { get; init; }

    public ActionMetadataBrandingId Branding { get; init; }

    public TextRange Range { get; init; }
}
