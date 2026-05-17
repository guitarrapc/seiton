using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>
/// Detects <c>contains('literal', attacker-controllable-context)</c> patterns
/// where substring matching may allow condition bypass.
/// </summary>
public sealed class UnsoundContainsRule() : RuleBase(RuleId.UnsoundContains)
{
    private const string ErrorMessage = "contains() with user-controllable context is vulnerable to substring bypass; use exact match or fromJSON() array contains";
    private const string InfoMessage = "contains() with context reference may be vulnerable to substring bypass; consider exact match or fromJSON() array contains";

    private static ReadOnlySpan<byte> ContainsFuncName => "contains"u8;

    // Exact user-controllable top-level context+property combinations
    private static ReadOnlySpan<byte> Actor => "actor"u8;
    private static ReadOnlySpan<byte> BaseRef => "base_ref"u8;
    private static ReadOnlySpan<byte> HeadRef => "head_ref"u8;
    private static ReadOnlySpan<byte> Ref => "ref"u8;
    private static ReadOnlySpan<byte> RefName => "ref_name"u8;
    private static ReadOnlySpan<byte> Sha => "sha"u8;
    private static ReadOnlySpan<byte> TriggeringActor => "triggering_actor"u8;

    public override string Name => "Unsound Contains Rule";

    public override bool SupportsDocumentKind(DocumentKind documentKind) => documentKind == DocumentKind.Workflow;

    public override void VisitJobPre(Job job)
    {
        CheckCondition(job.If, job, null);

        if (job.Snapshot is { } snapshot)
        {
            CheckCondition(snapshot.If, job, null);
        }
    }

    public override void VisitStep(Step step)
    {
        CheckCondition(step.If, null, step);
    }

    private void CheckCondition(StringNodeId condition, Job? job, Step? step)
    {
        if (!condition.HasValue || Config.Utf8Yaml is null)
        {
            return;
        }

        var raw = Arena.GetStringValue(condition);
        if (raw.Length == 0)
        {
            return;
        }

        // Fast pre-filter: skip parsing if "contains" keyword is not present (case-insensitive scan)
        if (!ContainsAsciiIgnoreCase(raw, ContainsFuncName))
        {
            return;
        }

        // Extract the expression body (content between ${{ and }})
        // If condition doesn't have ${{ }}, GitHub treats the whole value as an expression
        ReadOnlySpan<byte> exprBody;
        if (!ExpressionScanHelpers.TryExtractExpressionBody(raw, out var extracted))
        {
            exprBody = raw;
        }
        else
        {
            exprBody = extracted;
        }

        // Parse the expression (cached by content hash)
        var result = Config.ParseExpression(exprBody);
        if (!result.HasRoot)
        {
            return;
        }

        // Walk the AST looking for contains(string_literal, context_ref) patterns
        ScanForUnsoundContains(result, exprBody, job, step, condition);
    }

    private void ScanForUnsoundContains(ExpressionParseResult result, ReadOnlySpan<byte> exprBytes, Job? job, Step? step, StringNodeId condition)
    {
        var nodes = result.Nodes;
        var arguments = result.Arguments;

        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            if (node.Kind != ExpressionNodeKind.FunctionCall || node.ArgCount != 2)
            {
                continue;
            }

            // Check if the function name is "contains"
            var funcNameNodeId = node.Left;
            if (funcNameNodeId < 0 || funcNameNodeId >= nodes.Length)
            {
                continue;
            }

            var funcNameNode = nodes[funcNameNodeId];
            if (funcNameNode.Kind != ExpressionNodeKind.Identifier)
            {
                continue;
            }

            if (!IsContainsFunction(funcNameNode.Token, exprBytes))
            {
                continue;
            }

            // Get first argument - must be a string literal for this rule to fire
            var arg0Index = node.ArgStart;
            if (arg0Index < 0 || arg0Index >= arguments.Length)
            {
                continue;
            }

            var arg0NodeId = arguments[arg0Index];
            if (arg0NodeId < 0 || arg0NodeId >= nodes.Length)
            {
                continue;
            }

            if (nodes[arg0NodeId].Kind != ExpressionNodeKind.StringLiteral)
            {
                // fromJSON() or other non-literal first arg - this is array contains, not substring
                continue;
            }

            // Get second argument - check if it references a context
            var arg1Index = node.ArgStart + 1;
            if (arg1Index < 0 || arg1Index >= arguments.Length)
            {
                continue;
            }

            var arg1NodeId = arguments[arg1Index];
            var isUserControllable = IsUserControllableContext(arg1NodeId, nodes, arguments, exprBytes);

            // Report diagnostic
            var diagRange = Arena.GetStringRange(condition);
            if (isUserControllable)
            {
                if (job is not null)
                {
                    AddJobError(job, ErrorMessage, diagRange);
                }
                else if (step is not null)
                {
                    AddStepError(step, ErrorMessage, diagRange);
                }
            }
            else
            {
                // Check if it's any context access at all (not just a literal)
                if (IsAnyContextAccess(arg1NodeId, nodes))
                {
                    if (job is not null)
                    {
                        AddJobInfo(job, InfoMessage, diagRange);
                    }
                    else if (step is not null)
                    {
                        AddStepInfo(step, InfoMessage, diagRange);
                    }
                }
            }
        }
    }

    private static bool IsContainsFunction(Utf8Slice token, ReadOnlySpan<byte> exprBytes)
    {
        if (token.Length != ContainsFuncName.Length)
        {
            return false;
        }

        var tokenSpan = token.AsSpan(exprBytes);
        return EqualsAsciiIgnoreCase(tokenSpan, ContainsFuncName);
    }

    /// <summary>Checks if a node resolves to a user-controllable context.</summary>
    private static bool IsUserControllableContext(int nodeId, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> exprBytes)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];

        // Nested function call - check if it's a contains() with user-controllable second arg
        if (node.Kind == ExpressionNodeKind.FunctionCall && node.ArgCount == 2)
        {
            var nestedArg1Index = node.ArgStart + 1;
            if (nestedArg1Index >= 0 && nestedArg1Index < arguments.Length)
            {
                return IsUserControllableContext(arguments[nestedArg1Index], nodes, arguments, exprBytes);
            }

            return false;
        }

        // Index access: env['MY_VAR'], github['ref'], inputs['target']
        if (node.Kind == ExpressionNodeKind.IndexAccess)
        {
            return CheckIndexAccessUserControllable(node, nodes, exprBytes);
        }

        // Member access chain: reconstruct context path
        if (node.Kind == ExpressionNodeKind.MemberAccess)
        {
            return CheckMemberAccessUserControllable(nodeId, nodes, exprBytes);
        }

        // Single identifier like "env" won't match our patterns (we need env.X)
        return false;
    }

    private static bool CheckMemberAccessUserControllable(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> exprBytes)
    {
        // Build the context path by walking the member access chain
        // Member access: node.Left = base, node.Token = member name
        // For "github.actor": root is Identifier("github"), then MemberAccess with Token("actor")

        // First, find the root identifier and collect member tokens
        var current = nodeId;
        Span<int> memberStack = stackalloc int[8]; // max depth for our checks
        var depth = 0;

        while (current >= 0 && current < nodes.Length && nodes[current].Kind == ExpressionNodeKind.MemberAccess)
        {
            if (depth >= memberStack.Length)
            {
                return false; // Too deep for our known patterns
            }

            memberStack[depth++] = current;
            current = nodes[current].Left;
        }

        if (current < 0 || current >= nodes.Length || nodes[current].Kind != ExpressionNodeKind.Identifier)
        {
            return false;
        }

        var rootToken = nodes[current].Token.AsSpan(exprBytes);

        // Check "env.*" pattern (any env context is user-controllable)
        if (EqualsAsciiIgnoreCase(rootToken, "env"u8) && depth >= 1)
        {
            return true;
        }

        // Check "inputs.*" pattern
        if (EqualsAsciiIgnoreCase(rootToken, "inputs"u8) && depth >= 1)
        {
            return true;
        }

        // Check "github.X" patterns
        if (EqualsAsciiIgnoreCase(rootToken, "github"u8) && depth >= 1)
        {
            // The first member access token is the property name
            var firstMemberNode = nodes[memberStack[depth - 1]];
            var propToken = firstMemberNode.Token.AsSpan(exprBytes);

            if (EqualsAsciiIgnoreCase(propToken, Actor) ||
                EqualsAsciiIgnoreCase(propToken, BaseRef) ||
                EqualsAsciiIgnoreCase(propToken, HeadRef) ||
                EqualsAsciiIgnoreCase(propToken, Ref) ||
                EqualsAsciiIgnoreCase(propToken, RefName) ||
                EqualsAsciiIgnoreCase(propToken, Sha) ||
                EqualsAsciiIgnoreCase(propToken, TriggeringActor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAnyContextAccess(int nodeId, ExpressionNode[] nodes)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var kind = nodes[nodeId].Kind;
        return kind == ExpressionNodeKind.MemberAccess
            || kind == ExpressionNodeKind.Identifier
            || kind == ExpressionNodeKind.IndexAccess;
    }

    /// <summary>Checks index-style access like env['MY_VAR'], github['ref'], inputs['target'].</summary>
    private static bool CheckIndexAccessUserControllable(ExpressionNode node, ExpressionNode[] nodes, ReadOnlySpan<byte> exprBytes)
    {
        var baseId = node.Left;
        var indexId = node.Right;

        if (baseId < 0 || baseId >= nodes.Length || indexId < 0 || indexId >= nodes.Length)
        {
            return false;
        }

        var baseNode = nodes[baseId];
        if (baseNode.Kind != ExpressionNodeKind.Identifier)
        {
            return false;
        }

        var rootToken = baseNode.Token.AsSpan(exprBytes);

        // env['X'] - any env context is user-controllable
        if (EqualsAsciiIgnoreCase(rootToken, "env"u8))
        {
            return true;
        }

        // inputs['X'] - any inputs context is user-controllable
        if (EqualsAsciiIgnoreCase(rootToken, "inputs"u8))
        {
            return true;
        }

        // github['X'] - check specific property names
        if (EqualsAsciiIgnoreCase(rootToken, "github"u8))
        {
            var indexNode = nodes[indexId];
            if (indexNode.Kind != ExpressionNodeKind.StringLiteral)
            {
                return false;
            }

            var propName = GetStringLiteralContent(indexNode.Token, exprBytes);
            return EqualsAsciiIgnoreCase(propName, Actor) ||
                   EqualsAsciiIgnoreCase(propName, BaseRef) ||
                   EqualsAsciiIgnoreCase(propName, HeadRef) ||
                   EqualsAsciiIgnoreCase(propName, Ref) ||
                   EqualsAsciiIgnoreCase(propName, RefName) ||
                   EqualsAsciiIgnoreCase(propName, Sha) ||
                   EqualsAsciiIgnoreCase(propName, TriggeringActor);
        }

        return false;
    }

    /// <summary>Gets the content of a string literal (inside the quotes).</summary>
    private static ReadOnlySpan<byte> GetStringLiteralContent(Utf8Slice token, ReadOnlySpan<byte> exprBytes)
    {
        var span = token.AsSpan(exprBytes);
        if (span.Length >= 2 && span[0] == (byte)'\'' && span[span.Length - 1] == (byte)'\'')
        {
            return span[1..^1];
        }

        return span;
    }
}
