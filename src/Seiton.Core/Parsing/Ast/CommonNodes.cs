namespace Seiton.Core.Parsing.Ast;

public sealed class StringNode
{
    public Utf8Slice Value { get; init; }

    public bool Quoted { get; init; }

    public StringNode? Expression { get; init; }

    public TextRange Range { get; init; }
}

public sealed class BoolNode
{
    public bool Value { get; init; }

    public StringNode? Expression { get; init; }

    public TextRange Range { get; init; }
}

public sealed class IntNode
{
    public long Value { get; init; }

    public StringNode? Expression { get; init; }

    public TextRange Range { get; init; }
}

public sealed class FloatNode
{
    public double Value { get; init; }

    public StringNode? Expression { get; init; }

    public TextRange Range { get; init; }
}
