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
/// Depth-first traversal of an expression AST produced by <see cref="ExpressionParser"/> (Spec §6.5).
/// </summary>
public static class ExpressionVisitor
{
    /// <summary>
    /// Traverses the expression AST rooted at <paramref name="nodeId"/> depth-first.
    /// Calls <paramref name="visitor"/> twice per node:
    /// once with <c>entering = true</c> (before children) and once with <c>entering = false</c> (after children).
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
                // Left = base expression being accessed.
                VisitExprNode(node.Left, nodes, arguments, visitor, nodeId);
                break;

            case ExpressionNodeKind.IndexAccess:
                // Left = base expression, Right = index expression.
                VisitExprNode(node.Left, nodes, arguments, visitor, nodeId);
                VisitExprNode(node.Right, nodes, arguments, visitor, nodeId);
                break;

            case ExpressionNodeKind.FunctionCall:
                // Left = callee identifier (function name), then arguments in order.
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

            // Leaf nodes (Identifier, StringLiteral, NumberLiteral, BooleanLiteral, NullLiteral)
            // have no children — nothing to recurse into.
        }

        visitor(nodeId, node, parentId, entering: false);
    }
}
