namespace Seiton.Core.Parsing;

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
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Not,
    Negate,
}

public readonly record struct ExpressionNode(
    ExpressionNodeKind Kind,
    int Left,
    int Right,
    int ArgStart,
    int ArgCount,
    Utf8Slice Token,
    ExpressionOperator Operator);

public readonly record struct ExpressionParseResult(
    int RootNode,
    ExpressionNode[] Nodes,
    int[] Arguments,
    Diagnostic[] Diagnostics)
{
    public bool HasRoot => RootNode >= 0;
}
