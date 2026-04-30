namespace Seiton.Core.Parsing;

/// <summary>
/// Visitor callback for expression AST traversal (Spec §6.5).
/// </summary>
/// <param name="nodeId">Index into <see cref="ExpressionParseResult.Nodes"/> for the current node.</param>
/// <param name="node">The current <see cref="ExpressionNode"/>.</param>
/// <param name="parentId">Index of the parent node, or -1 for the root.</param>
/// <param name="entering">
/// <c>true</c> before visiting children (pre-order); <c>false</c> after visiting children (post-order).
/// </param>
/// <remarks>
/// The <paramref name="nodeId"/> parameter is an addition over the minimal spec signature to enable
/// callee-vs-argument discrimination: a callback can detect whether an <see cref="ExpressionNodeKind.Identifier"/>
/// is a function name by checking <c>nodes[parentId].Kind == FunctionCall &amp;&amp; nodes[parentId].Left == nodeId</c>.
/// </remarks>
public delegate void ExprNodeVisitor(int nodeId, ExpressionNode node, int parentId, bool entering);

/// <summary>
/// Structural visitor interface for zero-allocation expression AST traversal (Spec §6.5).
/// Implement this on a <c>ref struct</c> to capture <see cref="ReadOnlySpan{T}"/> state without heap allocation.
/// In C# 13 / .NET 9+, <c>ref struct</c> types can implement interfaces.
/// Callers pass the implementor as <c>ref TVisitor</c> to avoid boxing.
/// </summary>
public interface IExprNodeVisitor
{
    /// <summary>Called for each node during depth-first traversal, once on entry and once on exit.</summary>
    public void Visit(int nodeId, ExpressionNode node, int parentId, bool entering);
}

/// <summary>
/// Depth-first traversal of an expression AST produced by <see cref="ExpressionParser"/> (Spec §6.5).
/// </summary>
public static class ExpressionVisitor
{
    /// <summary>
    /// Traverses the expression AST rooted at <paramref name="nodeId"/> depth-first.
    /// Calls <paramref name="visitor"/> twice per node:
    /// once with <c>entering = true</c> (before children) and once with <c>entering = false</c> (after children).
    /// <para>
    /// Use this overload for simple cases where all captured state is heap-allocated (no <see cref="ReadOnlySpan{T}"/>).
    /// For callers that need to capture <see cref="ReadOnlySpan{T}"/> without allocation, use
    /// <see cref="VisitExprNode{TVisitor}(int, ExpressionNode[], int[], ref TVisitor, int)"/> instead.
    /// </para>
    /// </summary>
    /// <param name="nodeId">Root index into <paramref name="nodes"/>. Pass -1 or out-of-range to no-op.</param>
    /// <param name="nodes">The flat node array from <see cref="ExpressionParseResult.Nodes"/>.</param>
    /// <param name="arguments">The argument index array from <see cref="ExpressionParseResult.Arguments"/>.</param>
    /// <param name="visitor">Callback invoked for each node (pre and post).</param>
    /// <param name="parentId">Index of the parent; -1 for the root call.</param>
    public static void VisitExprNode(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ExprNodeVisitor visitor,
        int parentId = -1)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return;
        }

        var node = nodes[nodeId];
        visitor(nodeId, node, parentId, entering: true);

        switch (node.Kind)
        {
            case ExpressionNodeKind.Unary:
                VisitExprNode(node.Left, nodes, arguments, visitor, nodeId);
                break;

            case ExpressionNodeKind.Binary:
                VisitExprNode(node.Left, nodes, arguments, visitor, nodeId);
                VisitExprNode(node.Right, nodes, arguments, visitor, nodeId);
                break;

            case ExpressionNodeKind.MemberAccess:
            case ExpressionNodeKind.WildcardAccess:
                VisitExprNode(node.Left, nodes, arguments, visitor, nodeId);
                break;

            case ExpressionNodeKind.IndexAccess:
                VisitExprNode(node.Left, nodes, arguments, visitor, nodeId);
                VisitExprNode(node.Right, nodes, arguments, visitor, nodeId);
                break;

            case ExpressionNodeKind.FunctionCall:
                VisitExprNode(node.Left, nodes, arguments, visitor, nodeId);
                for (var i = 0; i < node.ArgCount; i++)
                {
                    var argIndex = node.ArgStart + i;
                    if (argIndex >= 0 && argIndex < arguments.Length)
                    {
                        VisitExprNode(arguments[argIndex], nodes, arguments, visitor, nodeId);
                    }
                }
                break;
        }

        visitor(nodeId, node, parentId, entering: false);
    }

    /// <summary>
    /// Zero-allocation overload for callers that implement <see cref="IExprNodeVisitor"/> as a <c>ref struct</c>.
    /// The visitor is passed by <c>ref</c> to avoid boxing, and may hold <see cref="ReadOnlySpan{T}"/> fields.
    /// The <c>allows ref struct</c> anti-constraint (C# 13 / .NET 9+) permits <typeparamref name="TVisitor"/>
    /// to be a ref struct while still satisfying the <see cref="IExprNodeVisitor"/> interface.
    /// Interface dispatch uses a constrained virtual call, which the JIT can devirtualize for struct types.
    /// </summary>
    public static void VisitExprNode<TVisitor>(
        int nodeId,
        ExpressionNode[] nodes,
        int[] arguments,
        ref TVisitor visitor,
        int parentId = -1)
        where TVisitor : IExprNodeVisitor, allows ref struct
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return;
        }

        var node = nodes[nodeId];
        visitor.Visit(nodeId, node, parentId, entering: true);

        switch (node.Kind)
        {
            case ExpressionNodeKind.Unary:
                VisitExprNode(node.Left, nodes, arguments, ref visitor, nodeId);
                break;

            case ExpressionNodeKind.Binary:
                VisitExprNode(node.Left, nodes, arguments, ref visitor, nodeId);
                VisitExprNode(node.Right, nodes, arguments, ref visitor, nodeId);
                break;

            case ExpressionNodeKind.MemberAccess:
            case ExpressionNodeKind.WildcardAccess:
                VisitExprNode(node.Left, nodes, arguments, ref visitor, nodeId);
                break;

            case ExpressionNodeKind.IndexAccess:
                VisitExprNode(node.Left, nodes, arguments, ref visitor, nodeId);
                VisitExprNode(node.Right, nodes, arguments, ref visitor, nodeId);
                break;

            case ExpressionNodeKind.FunctionCall:
                VisitExprNode(node.Left, nodes, arguments, ref visitor, nodeId);
                for (var i = 0; i < node.ArgCount; i++)
                {
                    var argIndex = node.ArgStart + i;
                    if (argIndex >= 0 && argIndex < arguments.Length)
                    {
                        VisitExprNode(arguments[argIndex], nodes, arguments, ref visitor, nodeId);
                    }
                }
                break;
        }

        visitor.Visit(nodeId, node, parentId, entering: false);
    }
}
