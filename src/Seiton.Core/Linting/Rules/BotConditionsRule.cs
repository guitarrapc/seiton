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
    private const string InfoMessage = "bot exclusion check uses spoofable context; consider github.event.pull_request.user.login or github.event.pull_request.user.id if available for this trigger";
    // Spoofable contexts
    private static ReadOnlySpan<byte> Actor => "actor"u8;
    private static ReadOnlySpan<byte> TriggeringActor => "triggering_actor"u8;
    private static ReadOnlySpan<byte> ActorId => "actor_id"u8;

    // Spoofable deep contexts: github.event.pull_request.sender.login
    private static ReadOnlySpan<byte> Event => "event"u8;
    private static ReadOnlySpan<byte> PullRequest => "pull_request"u8;
    private static ReadOnlySpan<byte> Sender => "sender"u8;
    private static ReadOnlySpan<byte> Login => "login"u8;
    private static ReadOnlySpan<byte> UserId => "id"u8;

    // Non-spoofable contexts (trigger-author): github.event.pull_request.user.login/id
    private static ReadOnlySpan<byte> User => "user"u8;

    // Known bot suffixes
    private static ReadOnlySpan<byte> BotSuffix => "[bot]"u8;

    // whether bot-condition diagnostics are actionable for this workflow's triggers
    private bool _emitBotConditionDiagnostics;

    public override string Name => "Bot Conditions Rule";

    public override bool SupportsDocumentKind(DocumentKind documentKind) => documentKind == DocumentKind.Workflow;

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        _emitBotConditionDiagnostics = ShouldEmitBotConditionDiagnostics(workflow);
    }

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
        // or one of the small set of known bot IDs. The AST scan does the exact
        // context-path discrimination, including mixed dot/index access forms.
        var hasBotLoginLiteral = ContainsAsciiIgnoreCase(raw, BotSuffix);
        var hasKnownBotIdLiteral = BotActors.ContainsKnownBotId(raw);

        if (!hasBotLoginLiteral && !hasKnownBotIdLiteral)
        {
            return;
        }

        // Workflow-level suppression is decided once in VisitWorkflowPre.
        // If diagnostics are not actionable for this trigger set, skip parsing entirely.
        if (!_emitBotConditionDiagnostics)
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
        var hasOr = HasOrOperator(nodes);

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

            // Determine which side is the spoofable context and which is the literal
            int literalId;
            if (IsBotComparison(leftId, rightId, nodes, exprBytes))
            {
                literalId = rightId;
            }
            else if (IsBotComparison(rightId, leftId, nodes, exprBytes))
            {
                literalId = leftId;
            }
            else
            {
                continue;
            }

            // If the same expression has a non-spoofable context check with the same literal
            // AND-conjoined, suppress. Skip suppression when OR operators exist (non-spoofable
            // check on the other side of OR does not mitigate the spoofable branch).
            if (!hasOr && HasNonSpoofableConjunction(literalId, nodes, exprBytes))
            {
                continue;
            }

            // != (exclusion pattern) emits info; == (privilege grant) emits warning
            // Suppress when triggers are not PR-only (no PR context, or mixed triggers where github.actor is the only cross-trigger bot check).
            if (!_emitBotConditionDiagnostics)
            {
                continue;
            }

            var diagRange = Arena.GetStringRange(condition);
            if (node.Operator == ExpressionOperator.NotEqual)
            {
                if (!IsStrictDetectionEnabled())
                {
                    continue;
                }

                if (job is not null)
                {
                    AddJobInfo(job, InfoMessage, diagRange);
                }
                else if (step is not null)
                {
                    AddStepInfo(step, InfoMessage, diagRange);
                }
            }
            else
            {
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
    /// Returns true if the expression contains any OR operator, making flat conjunction
    /// scanning unsafe (a non-spoofable check on the other side of OR does not mitigate).
    /// </summary>
    private static bool HasOrOperator(ExpressionNode[] nodes)
    {
        for (var i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Kind == ExpressionNodeKind.Binary && nodes[i].Operator == ExpressionOperator.Or)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when the workflow triggers are PR-only, making
    /// <c>github.event.pull_request.user.login</c> a viable alternative to spoofable actor contexts.
    /// Mixed or non-PR triggers suppress diagnostics because <c>github.actor</c> is often the only
    /// cross-trigger bot check.
    /// </summary>
    private bool ShouldEmitBotConditionDiagnostics(Workflow workflow)
    {
        var hasPrEvent = false;
        var hasNonPrTrigger = false;

        for (var i = 0; i < workflow.On.Count; i++)
        {
            switch (workflow.On[i])
            {
                case WebhookEvent webhook:
                    var hook = Arena.GetStringValue(webhook.Hook);
                    if (WebhookTypes.TryGet(hook, out _, out var spec) && IsPullRequestWebhook(spec.Id))
                    {
                        hasPrEvent = true;
                    }
                    else
                    {
                        hasNonPrTrigger = true;
                    }

                    break;
                default:
                    hasNonPrTrigger = true;
                    break;
            }
        }

        return hasPrEvent && !hasNonPrTrigger;
    }

    private static bool IsPullRequestWebhook(WebhookTypes.EventId eventId) =>
        eventId is WebhookTypes.EventId.PullRequest
            or WebhookTypes.EventId.PullRequestTarget
            or WebhookTypes.EventId.PullRequestReview
            or WebhookTypes.EventId.PullRequestReviewComment;

    private bool IsStrictDetectionEnabled() =>
        Config.GetRuleConfig(Id)?.StrictDetection == true;

    /// <summary>
    /// Checks if the expression contains a non-spoofable context (trigger-author)
    /// comparison with the same literal value, indicating the spoofable check is mitigated.
    /// </summary>
    private static bool HasNonSpoofableConjunction(int spoofableLiteralId, ExpressionNode[] nodes, ReadOnlySpan<byte> exprBytes)
    {
        var spoofableLiteral = nodes[spoofableLiteralId];

        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            if (node.Kind != ExpressionNodeKind.Binary || node.Operator != ExpressionOperator.Equal)
            {
                continue;
            }

            // Skip negated comparisons: !(x == y) is NOT a mitigation
            var isNegated = false;
            for (var j = 0; j < nodes.Length; j++)
            {
                if (nodes[j].Kind == ExpressionNodeKind.Unary && nodes[j].Operator == ExpressionOperator.Not && nodes[j].Left == i)
                {
                    isNegated = true;
                    break;
                }
            }

            if (isNegated)
            {
                continue;
            }

            var leftId = node.Left;
            var rightId = node.Right;

            if (leftId < 0 || leftId >= nodes.Length || rightId < 0 || rightId >= nodes.Length)
            {
                continue;
            }

            // Check: non-spoofable context == same_literal (either order)
            if (IsNonSpoofableBotComparison(leftId, rightId, spoofableLiteral, nodes, exprBytes) ||
                IsNonSpoofableBotComparison(rightId, leftId, spoofableLiteral, nodes, exprBytes))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if contextNodeId is a non-spoofable trigger-author context and literalNodeId
    /// matches the same literal value as the spoofable comparison.
    /// </summary>
    private static bool IsNonSpoofableBotComparison(int contextNodeId, int literalNodeId, ExpressionNode spoofableLiteral, ExpressionNode[] nodes, ReadOnlySpan<byte> exprBytes)
    {
        if (!IsNonSpoofableContext(contextNodeId, nodes, exprBytes))
        {
            return false;
        }

        var literalNode = nodes[literalNodeId];

        // Both must be the same kind of literal
        if (literalNode.Kind != spoofableLiteral.Kind)
        {
            return false;
        }

        // Compare literal values byte-by-byte
        var literalSpan = literalNode.Token.AsSpan(exprBytes);
        var spoofableSpan = spoofableLiteral.Token.AsSpan(exprBytes);
        return literalSpan.SequenceEqual(spoofableSpan);
    }

    /// <summary>
    /// Checks if a node is a non-spoofable trigger-author context:
    /// github.event.pull_request.user.login or github.event.pull_request.user.id
    /// </summary>
    private static bool IsNonSpoofableContext(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> exprBytes)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        return MatchesGitHubPath(nodeId, nodes, exprBytes, Event, PullRequest, User, Login)
            || MatchesGitHubPath(nodeId, nodes, exprBytes, Event, PullRequest, User, UserId);
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

        return MatchesGitHubPath(nodeId, nodes, exprBytes, Actor)
            || MatchesGitHubPath(nodeId, nodes, exprBytes, TriggeringActor)
            || MatchesGitHubPath(nodeId, nodes, exprBytes, Event, PullRequest, Sender, Login);
    }

    private static bool IsSpoofableActorIdContext(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> exprBytes)
    {
        return MatchesGitHubPath(nodeId, nodes, exprBytes, ActorId)
            || MatchesGitHubPath(nodeId, nodes, exprBytes, Event, PullRequest, Sender, UserId);
    }

    /// <summary>Gets the content of a string literal (inside the quotes).</summary>
    private static ReadOnlySpan<byte> GetStringLiteralContent(Utf8Slice token, ReadOnlySpan<byte> exprBytes)
    {
        // ExpressionParser.ParseStringLiteral stores the token slice inside the quotes.
        return token.AsSpan(exprBytes);
    }

    private static bool EndsWithBotSuffix(ReadOnlySpan<byte> value)
    {
        if (value.Length < BotSuffix.Length)
        {
            return false;
        }

        return value[^BotSuffix.Length..].SequenceEqual(BotSuffix);
    }

    private static bool MatchesGitHubPath(
        int nodeId,
        ExpressionNode[] nodes,
        ReadOnlySpan<byte> exprBytes,
        ReadOnlySpan<byte> segment1)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var current = nodeId;
        if (!TryMatchPathSegment(ref current, nodes, exprBytes, segment1))
        {
            return false;
        }

        return IsGitHubRoot(current, nodes, exprBytes);
    }

    private static bool MatchesGitHubPath(
        int nodeId,
        ExpressionNode[] nodes,
        ReadOnlySpan<byte> exprBytes,
        ReadOnlySpan<byte> segment1,
        ReadOnlySpan<byte> segment2,
        ReadOnlySpan<byte> segment3,
        ReadOnlySpan<byte> segment4)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var current = nodeId;
        if (!TryMatchPathSegment(ref current, nodes, exprBytes, segment4)
            || !TryMatchPathSegment(ref current, nodes, exprBytes, segment3)
            || !TryMatchPathSegment(ref current, nodes, exprBytes, segment2)
            || !TryMatchPathSegment(ref current, nodes, exprBytes, segment1))
        {
            return false;
        }

        return IsGitHubRoot(current, nodes, exprBytes);
    }

    private static bool TryMatchPathSegment(
        ref int nodeId,
        ExpressionNode[] nodes,
        ReadOnlySpan<byte> exprBytes,
        ReadOnlySpan<byte> expectedSegment)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        if (node.Kind == ExpressionNodeKind.MemberAccess)
        {
            if (!EqualsAsciiIgnoreCase(node.Token.AsSpan(exprBytes), expectedSegment))
            {
                return false;
            }

            nodeId = node.Left;
            return true;
        }

        if (node.Kind != ExpressionNodeKind.IndexAccess)
        {
            return false;
        }

        var indexId = node.Right;
        if (indexId < 0 || indexId >= nodes.Length)
        {
            return false;
        }

        var indexNode = nodes[indexId];
        if (indexNode.Kind != ExpressionNodeKind.StringLiteral)
        {
            return false;
        }

        if (!EqualsAsciiIgnoreCase(GetStringLiteralContent(indexNode.Token, exprBytes), expectedSegment))
        {
            return false;
        }

        nodeId = node.Left;
        return true;
    }

    private static bool IsGitHubRoot(int nodeId, ExpressionNode[] nodes, ReadOnlySpan<byte> exprBytes)
    {
        if (nodeId < 0 || nodeId >= nodes.Length)
        {
            return false;
        }

        var node = nodes[nodeId];
        return node.Kind == ExpressionNodeKind.Identifier
            && EqualsAsciiIgnoreCase(node.Token.AsSpan(exprBytes), "github"u8);
    }

}
