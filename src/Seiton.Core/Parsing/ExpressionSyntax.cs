namespace Seiton.Core.Parsing;

/// <summary>Kind of node in the expression AST.</summary>
public enum ExpressionNodeKind
{
    Identifier,
    StringLiteral,
    NumberLiteral,
    BooleanLiteral,
    NullLiteral,
    MemberAccess,
    IndexAccess,
    WildcardAccess,
    FunctionCall,
    Unary,
    Binary,
}

/// <summary>Operator kind for unary and binary expression nodes.</summary>
public enum ExpressionOperator
{
    None,
    Or,
    And,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Not,
}

/// <summary>A single node in the expression AST (flat array representation).</summary>
public readonly record struct ExpressionNode(
    ExpressionNodeKind Kind,
    int Left,
    int Right,
    int ArgStart,
    int ArgCount,
    Utf8Slice Token,
    ExpressionOperator Operator);

/// <summary>Result of parsing a GitHub Actions expression string.</summary>
public readonly record struct ExpressionParseResult(
    int RootNode,
    ReadOnlyMemory<ExpressionNode> Nodes,
    ReadOnlyMemory<int> Arguments,
    ReadOnlyMemory<Diagnostic> Diagnostics)
{
    /// <summary>Gets whether the parse produced a valid root node.</summary>
    public bool HasRoot => RootNode >= 0;
}
