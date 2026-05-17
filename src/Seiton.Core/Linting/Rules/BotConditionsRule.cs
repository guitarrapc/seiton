using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Linting.Rules;

/// <summary>
/// Detects spoofable bot actor comparisons like <c>github.actor == 'dependabot[bot]'</c>.
/// The actor name can be spoofed; prefer a trigger-author context such as <c>github.event.pull_request.user.login</c>.
/// </summary>
public sealed class BotConditionsRule() : RuleBase(RuleId.BotConditions)
{
    private const string WarningMessage = "bot actor check uses spoofable context; prefer github.event.pull_request.user.login, github.event.pull_request.user.id, or another trigger-author context";
    // Spoofable contexts
    private static ReadOnlySpan<byte> Actor => "actor"u8;
    private static ReadOnlySpan<byte> TriggeringActor => "triggering_actor"u8;
    private static ReadOnlySpan<byte> ActorId => "actor_id"u8;

    // Spoofable deep contexts: github.event.pull_request.sender.login
    private static ReadOnlySpan<byte> Event => "event"u8;
    private static ReadOnlySpan<byte> PullRequest => "pull_request"u8;
    private static ReadOnlySpan<byte> Sender => "sender"u8;
    private static ReadOnlySpan<byte> SenderId => "sender.id"u8;
    private static ReadOnlySpan<byte> Login => "login"u8;
    private static ReadOnlySpan<byte> UserId => "id"u8;

    // Known bot suffixes
    private static ReadOnlySpan<byte> BotSuffix => "[bot]"u8;

    public override string Name => "Bot Conditions Rule";

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

        // Fast pre-filter: skip parsing unless condition contains a bot login literal,
        // or an actor ID context together with a known bot ID.
        var hasBotLoginLiteral = ContainsAsciiIgnoreCase(raw, BotSuffix);
        var hasBotActorIdCheck = BotActors.ContainsKnownBotId(raw) &&
            (ContainsAsciiIgnoreCase(raw, ActorId) || ContainsAsciiIgnoreCase(raw, SenderId));

        if (!hasBotLoginLiteral && !hasBotActorIdCheck)
        {
            return;
        }

        // Extract the expression body
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

        // Walk the AST looking for spoofable bot comparisons
        ScanForBotConditions(result, exprBody, job, step, condition);
    }

    private void ScanForBotConditions(ExpressionParseResult result, ReadOnlySpan<byte> exprBytes, Job? job, Step? step, StringNodeId condition)
    {
        var nodes = result.Nodes;

        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            if (node.Kind != ExpressionNodeKind.Binary)
            {
                continue;
            }

            // Only check == and != comparisons
            if (node.Operator != ExpressionOperator.Equal && node.Operator != ExpressionOperator.NotEqual)
            {
                continue;
            }

            var leftId = node.Left;
            var rightId = node.Right;

            if (leftId < 0 || leftId >= nodes.Length || rightId < 0 || rightId >= nodes.Length)
            {
                continue;
            }

            // Check pattern: spoofable_context == 'bot_name' or 'bot_name' == spoofable_context
            if (IsBotComparison(leftId, rightId, nodes, exprBytes) ||
                IsBotComparison(rightId, leftId, nodes, exprBytes))
            {
                var diagRange = Arena.GetStringRange(condition);

                if (job is not null)
                {
                    AddJobWarning(job, WarningMessage, diagRange);
                }
                else if (step is not null)
                {
                    AddStepWarning(step, WarningMessage, diagRange);
                }
            }
        }
    }

    /// <summary>
    /// Checks if contextNodeId is a spoofable context and literalNodeId is a bot-related literal.
    /// </summary>
    private static bool IsBotComparison(int contextNodeId, int literalNodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> exprBytes)
    {
        var literalNode = nodes[literalNodeId];
        if (literalNode.Kind != ExpressionNodeKind.StringLiteral && literalNode.Kind != ExpressionNodeKind.NumberLiteral)
        {
            return false;
        }

        if (IsSpoofableActorContext(contextNodeId, nodes, exprBytes))
        {
            if (literalNode.Kind == ExpressionNodeKind.StringLiteral)
            {
                var literalSpan = GetStringLiteralContent(literalNode.Token, exprBytes);
                return EndsWithBotSuffix(literalSpan);
            }

            return false;
        }

        if (!IsSpoofableActorIdContext(contextNodeId, nodes, exprBytes))
        {
            return false;
        }

        return literalNode.Kind switch
        {
            ExpressionNodeKind.StringLiteral => BotActors.IsKnownBotId(GetStringLiteralContent(literalNode.Token, exprBytes)),
            ExpressionNodeKind.NumberLiteral => BotActors.IsKnownBotId(literalNode.Token.AsSpan(exprBytes)),
            _ => false,
        };
    }

    private static bool IsSpoofableActorContext(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> exprBytes)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];

        // Handle index-style access: github['actor'] or github['triggering_actor']
        if (node.Kind == ExpressionNodeKind.IndexAccess)
        {
            return IsIndexAccessSpoofableActor(node, nodes, exprBytes);
        }

        if (node.Kind != ExpressionNodeKind.MemberAccess)
        {
            return false;
        }

        // Walk the member access chain
        var current = nodeId;
        Span<int> memberStack = stackalloc int[8];
        var depth = 0;

        while (current >= 0 && current < nodes.Length && nodes[current].Kind == ExpressionNodeKind.MemberAccess)
        {
            if (depth >= memberStack.Length)
            {
                return false;
            }

            memberStack[depth++] = current;
            current = nodes[current].Left;
        }

        if (current < 0 || current >= nodes.Length || nodes[current].Kind != ExpressionNodeKind.Identifier)
        {
            return false;
        }

        var rootToken = nodes[current].Token.AsSpan(exprBytes);

        if (!EqualsAsciiIgnoreCase(rootToken, "github"u8))
        {
            return false;
        }

        if (depth < 1)
        {
            return false;
        }

        // github.actor or github.triggering_actor
        var prop1 = nodes[memberStack[depth - 1]].Token.AsSpan(exprBytes);
        if (depth == 1)
        {
            return EqualsAsciiIgnoreCase(prop1, Actor) ||
                   EqualsAsciiIgnoreCase(prop1, TriggeringActor);
        }

        // github.event.pull_request.sender.login (depth=4)
        if (depth == 4)
        {
            var prop2 = nodes[memberStack[depth - 2]].Token.AsSpan(exprBytes);
            var prop3 = nodes[memberStack[depth - 3]].Token.AsSpan(exprBytes);
            var prop4 = nodes[memberStack[depth - 4]].Token.AsSpan(exprBytes);

            return EqualsAsciiIgnoreCase(prop1, Event) &&
                   EqualsAsciiIgnoreCase(prop2, PullRequest) &&
                   EqualsAsciiIgnoreCase(prop3, Sender) &&
                   EqualsAsciiIgnoreCase(prop4, Login);
        }

        return false;
    }

    private static bool IsSpoofableActorIdContext(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> exprBytes)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];

        // Handle index-style access: github['actor_id']
        if (node.Kind == ExpressionNodeKind.IndexAccess)
        {
            return IsIndexAccessSpoofableActorId(node, nodes, exprBytes);
        }

        if (node.Kind != ExpressionNodeKind.MemberAccess)
        {
            return false;
        }

        var current = nodeId;
        Span<int> memberStack = stackalloc int[8];
        var depth = 0;

        while (current >= 0 && current < nodes.Length && nodes[current].Kind == ExpressionNodeKind.MemberAccess)
        {
            if (depth >= memberStack.Length)
            {
                return false;
            }

            memberStack[depth++] = current;
            current = nodes[current].Left;
        }

        if (current < 0 || current >= nodes.Length || nodes[current].Kind != ExpressionNodeKind.Identifier)
        {
            return false;
        }

        var rootToken = nodes[current].Token.AsSpan(exprBytes);
        if (!EqualsAsciiIgnoreCase(rootToken, "github"u8) || depth < 1)
        {
            return false;
        }

        var prop1 = nodes[memberStack[depth - 1]].Token.AsSpan(exprBytes);
        if (depth == 1)
        {
            return EqualsAsciiIgnoreCase(prop1, ActorId);
        }

        if (depth == 4)
        {
            var prop2 = nodes[memberStack[depth - 2]].Token.AsSpan(exprBytes);
            var prop3 = nodes[memberStack[depth - 3]].Token.AsSpan(exprBytes);
            var prop4 = nodes[memberStack[depth - 4]].Token.AsSpan(exprBytes);

            return EqualsAsciiIgnoreCase(prop1, Event) &&
                   EqualsAsciiIgnoreCase(prop2, PullRequest) &&
                   EqualsAsciiIgnoreCase(prop3, Sender) &&
                   EqualsAsciiIgnoreCase(prop4, UserId);
        }

        return false;
    }

    /// <summary>Gets the content of a string literal (inside the quotes).</summary>
    private static ReadOnlySpan<byte> GetStringLiteralContent(Utf8Slice token, ReadOnlySpan<byte> exprBytes)
    {
        // String literals in the expression include the surrounding quotes in the token
        var span = token.AsSpan(exprBytes);
        if (span.Length >= 2 && span[0] == (byte)'\'' && span[span.Length - 1] == (byte)'\'')
        {
            return span[1..^1];
        }

        return span;
    }

    private static bool EndsWithBotSuffix(ReadOnlySpan<byte> value)
    {
        if (value.Length < BotSuffix.Length)
        {
            return false;
        }

        return value[^BotSuffix.Length..].SequenceEqual(BotSuffix);
    }

    /// <summary>
    /// Checks index-style access like github['actor'] or github['triggering_actor'].
    /// Only handles depth-1 patterns where Left is Identifier("github") and Right is a StringLiteral.
    /// </summary>
    private static bool IsIndexAccessSpoofableActor(ExpressionNode node, ExpressionNode[] nodes, ReadOnlySpan<byte> exprBytes)
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

        if (!EqualsAsciiIgnoreCase(baseNode.Token.AsSpan(exprBytes), "github"u8))
        {
            return false;
        }

        var indexNode = nodes[indexId];
        if (indexNode.Kind != ExpressionNodeKind.StringLiteral)
        {
            return false;
        }

        var propName = GetStringLiteralContent(indexNode.Token, exprBytes);
        return EqualsAsciiIgnoreCase(propName, Actor) ||
               EqualsAsciiIgnoreCase(propName, TriggeringActor);
    }

    /// <summary>
    /// Checks index-style access like github['actor_id'].
    /// Only handles depth-1 patterns where Left is Identifier("github") and Right is a StringLiteral.
    /// </summary>
    private static bool IsIndexAccessSpoofableActorId(ExpressionNode node, ExpressionNode[] nodes, ReadOnlySpan<byte> exprBytes)
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

        if (!EqualsAsciiIgnoreCase(baseNode.Token.AsSpan(exprBytes), "github"u8))
        {
            return false;
        }

        var indexNode = nodes[indexId];
        if (indexNode.Kind != ExpressionNodeKind.StringLiteral)
        {
            return false;
        }

        var propName = GetStringLiteralContent(indexNode.Token, exprBytes);
        return EqualsAsciiIgnoreCase(propName, ActorId);
    }

}
