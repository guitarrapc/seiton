namespace Seiton.Core.Parsing;

public enum ExpressionSyntaxKind
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

public abstract record ExpressionSyntax(ExpressionSyntaxKind Kind);

public sealed record IdentifierSyntax(string Name)
    : ExpressionSyntax(ExpressionSyntaxKind.Identifier);

public sealed record StringLiteralSyntax(string Value)
    : ExpressionSyntax(ExpressionSyntaxKind.StringLiteral);

public sealed record NumberLiteralSyntax(string Value)
    : ExpressionSyntax(ExpressionSyntaxKind.NumberLiteral);

public sealed record BooleanLiteralSyntax(bool Value)
    : ExpressionSyntax(ExpressionSyntaxKind.BooleanLiteral);

public sealed record NullLiteralSyntax()
    : ExpressionSyntax(ExpressionSyntaxKind.NullLiteral);

public sealed record MemberAccessSyntax(ExpressionSyntax Target, string Member)
    : ExpressionSyntax(ExpressionSyntaxKind.MemberAccess);

public sealed record IndexAccessSyntax(ExpressionSyntax Target, ExpressionSyntax Index)
    : ExpressionSyntax(ExpressionSyntaxKind.IndexAccess);

public sealed record WildcardAccessSyntax(ExpressionSyntax Target)
    : ExpressionSyntax(ExpressionSyntaxKind.WildcardAccess);

public sealed record FunctionCallSyntax(ExpressionSyntax Callee, IReadOnlyList<ExpressionSyntax> Arguments)
    : ExpressionSyntax(ExpressionSyntaxKind.FunctionCall);

public sealed record UnarySyntax(string Operator, ExpressionSyntax Operand)
    : ExpressionSyntax(ExpressionSyntaxKind.Unary);

public sealed record BinarySyntax(ExpressionSyntax Left, string Operator, ExpressionSyntax Right)
    : ExpressionSyntax(ExpressionSyntaxKind.Binary);

public readonly record struct ExpressionParseResult(
    ExpressionSyntax? Root,
    Diagnostic[] Diagnostics);
