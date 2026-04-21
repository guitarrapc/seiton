namespace Seiton.Core.Parsing.Ast;

public sealed class ActionMetadata
{
    public StringNode? Name { get; init; }

    public StringNode? Description { get; init; }

    public IReadOnlyDictionary<Utf8String, ActionMetadataInput>? Inputs { get; init; }

    public IReadOnlyDictionary<Utf8String, ActionMetadataOutput>? Outputs { get; init; }

    public ActionMetadataRuns? Runs { get; init; }

    public ActionMetadataBranding? Branding { get; init; }

    public TextRange Range { get; init; }
}

public sealed class ActionMetadataInput
{
    public StringNode Name { get; init; } = null!;

    public StringNode? Description { get; init; }

    public BoolNode? Required { get; init; }

    public StringNode? Default { get; init; }

    public StringNode? DeprecationMessage { get; init; }

    public TextRange Range { get; init; }
}

public sealed class ActionMetadataOutput
{
    public StringNode Name { get; init; } = null!;

    public StringNode? Description { get; init; }

    public StringNode? Value { get; init; }

    public TextRange Range { get; init; }
}

public sealed class ActionMetadataRuns
{
    public StringNode? Using { get; init; }

    public StringNode? Main { get; init; }

    public StringNode? Pre { get; init; }

    public StringNode? Post { get; init; }

    public StringNode? PreIf { get; init; }

    public StringNode? PostIf { get; init; }

    public StringNode? Image { get; init; }

    public StringNode? Entrypoint { get; init; }

    public IReadOnlyList<StringNode>? Args { get; init; }

    public Env? Env { get; init; }

    public IReadOnlyList<Step>? Steps { get; init; }

    public TextRange Range { get; init; }
}

public sealed class ActionMetadataBranding
{
    public StringNode? Icon { get; init; }

    public StringNode? Color { get; init; }

    public TextRange Range { get; init; }
}
