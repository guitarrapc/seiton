using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting;

public sealed class GlobPatternRule : RuleBase
{
    public override string Id => "glob-pattern";

    public override string Name => "Glob Pattern Rule";

    public override void VisitEvent(Event ev)
    {
        if (ev is not WebhookEvent webhookEv || Config.Utf8Yaml is null)
        {
            return;
        }

        ValidateFilter(webhookEv, webhookEv.Branches);
        ValidateFilter(webhookEv, webhookEv.BranchesIgnore);
        ValidateFilter(webhookEv, webhookEv.Tags);
        ValidateFilter(webhookEv, webhookEv.TagsIgnore);
        ValidateFilter(webhookEv, webhookEv.Paths);
        ValidateFilter(webhookEv, webhookEv.PathsIgnore);
    }

    void ValidateFilter(WebhookEvent webhookEv, WebhookEventFilter? filter)
    {
        if (filter is null)
        {
            return;
        }

        var filterName = Decode(filter.Name.Value);
        for (var i = 0; i < filter.Values.Count; i++)
        {
            var valueNode = filter.Values[i];
            var pattern = valueNode.Value.AsSpan(Config.Utf8Yaml);
            if (valueNode.Expression is not null || pattern.IndexOf("${{"u8) >= 0)
            {
                continue;
            }

            if (TryGetInvalidReason(pattern, out var reason))
            {
                var patternText = Decode(valueNode.Value);
                AddEventError(
                    webhookEv,
                    $"event filter '{filterName}' has invalid glob pattern '{patternText}': {reason}",
                    valueNode.Range);
            }
        }
    }

    static bool TryGetInvalidReason(ReadOnlySpan<byte> pattern, out string reason)
    {
        var consecutiveStars = 0;
        var openBracketCount = 0;

        for (var i = 0; i < pattern.Length; i++)
        {
            var b = pattern[i];
            if (b == (byte)'*')
            {
                consecutiveStars++;
                if (consecutiveStars >= 3)
                {
                    reason = "consecutive '*' longer than '**' is not supported";
                    return true;
                }
            }
            else
            {
                consecutiveStars = 0;
            }

            if (b == (byte)'[')
            {
                openBracketCount++;
            }
            else if (b == (byte)']')
            {
                if (openBracketCount == 0)
                {
                    reason = "closing ']' without opening '['";
                    return true;
                }

                openBracketCount--;
            }
        }

        if (openBracketCount > 0)
        {
            reason = "'[' is not closed";
            return true;
        }

        reason = string.Empty;
        return false;
    }
}
