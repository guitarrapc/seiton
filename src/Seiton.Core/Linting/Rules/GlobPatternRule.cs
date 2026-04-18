using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

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

        var eventName = webhookEv.EventName.Value.AsSpan(Config.Utf8Yaml);
        if (!WebhookTypes.TryGet(eventName, out var normalizedEventName, out var spec))
        {
            return;
        }

        ValidateOptionAllowList(webhookEv, normalizedEventName, spec);
        ValidateTypeValues(webhookEv, normalizedEventName, spec);
        ValidateMutualExclusionFilters(webhookEv);

        ValidateFilter(webhookEv, webhookEv.Branches);
        ValidateFilter(webhookEv, webhookEv.BranchesIgnore);
        ValidateFilter(webhookEv, webhookEv.Tags);
        ValidateFilter(webhookEv, webhookEv.TagsIgnore);
        ValidateFilter(webhookEv, webhookEv.Paths);
        ValidateFilter(webhookEv, webhookEv.PathsIgnore);
    }

    void ValidateOptionAllowList(WebhookEvent webhookEv, string eventName, WebhookTypes.EventSpec spec)
    {
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.Types is not null && webhookEv.Types.Count > 0, "types"u8, webhookEv.Types?[0].Range);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.Branches is not null, "branches"u8, webhookEv.Branches?.Name.Range);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.BranchesIgnore is not null, "branches-ignore"u8, webhookEv.BranchesIgnore?.Name.Range);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.Tags is not null, "tags"u8, webhookEv.Tags?.Name.Range);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.TagsIgnore is not null, "tags-ignore"u8, webhookEv.TagsIgnore?.Name.Range);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.Paths is not null, "paths"u8, webhookEv.Paths?.Name.Range);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.PathsIgnore is not null, "paths-ignore"u8, webhookEv.PathsIgnore?.Name.Range);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.Workflows is not null && webhookEv.Workflows.Count > 0, "workflows"u8, webhookEv.Workflows?[0].Range);
    }

    void ValidateOptionAllowList(
        WebhookEvent webhookEv,
        string eventName,
        WebhookTypes.EventSpec spec,
        bool present,
        ReadOnlySpan<byte> optionName,
        TextRange? optionRange)
    {
        if (!present || spec.IsOptionAllowed(optionName))
        {
            return;
        }

        var optionText = DecodeAscii(optionName);
        AddEventError(
            webhookEv,
            $"event '{eventName}' does not support option '{optionText}'",
            optionRange ?? BuildEventLocation(webhookEv));
    }

    void ValidateTypeValues(WebhookEvent webhookEv, string eventName, WebhookTypes.EventSpec spec)
    {
        if (webhookEv.Types is null || webhookEv.Types.Count == 0)
        {
            return;
        }

        if (!spec.IsTypeOptionSupported())
        {
            AddEventError(
                webhookEv,
                $"event '{eventName}' does not support 'types'",
                webhookEv.Types[0].Range);
            return;
        }

        for (var i = 0; i < webhookEv.Types.Count; i++)
        {
            var typeNode = webhookEv.Types[i];
            var typeValue = typeNode.Value.AsSpan(Config.Utf8Yaml);
            if (typeNode.Expression is not null || typeValue.IndexOf("${{"u8) >= 0)
            {
                continue;
            }

            if (spec.IsTypeAllowed(typeValue))
            {
                continue;
            }

            var typeText = Decode(typeNode.Value);
            AddEventError(
                webhookEv,
                $"event '{eventName}' has unsupported activity type '{typeText}'",
                typeNode.Range);
        }
    }

    void ValidateMutualExclusionFilters(WebhookEvent webhookEv)
    {
        if (webhookEv.Branches is not null && webhookEv.BranchesIgnore is not null)
        {
            AddEventError(
                webhookEv,
                "event filter 'branches' and 'branches-ignore' cannot be used together",
                webhookEv.BranchesIgnore.Name.Range);
        }

        if (webhookEv.Tags is not null && webhookEv.TagsIgnore is not null)
        {
            AddEventError(
                webhookEv,
                "event filter 'tags' and 'tags-ignore' cannot be used together",
                webhookEv.TagsIgnore.Name.Range);
        }

        if (webhookEv.Paths is not null && webhookEv.PathsIgnore is not null)
        {
            AddEventError(
                webhookEv,
                "event filter 'paths' and 'paths-ignore' cannot be used together",
                webhookEv.PathsIgnore.Name.Range);
        }
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

    static string DecodeAscii(ReadOnlySpan<byte> utf8)
    {
        var chars = new char[utf8.Length];
        for (var i = 0; i < utf8.Length; i++)
        {
            chars[i] = (char)utf8[i];
        }

        return new string(chars);
    }
}
