using Seiton.Core.Generated;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates glob patterns in branch/path/tag filters for syntax errors.</summary>
public sealed class GlobPatternRule() : RuleBase(RuleId.GlobPattern)
{
    public override string Name => "Glob Pattern Rule";

    public override void VisitEvent(Event ev)
    {
        if (ev is not WebhookEvent webhookEv || Config.Utf8Yaml is null)
        {
            return;
        }

        var eventName = Arena.GetStringValue(webhookEv.EventName);
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

    private void ValidateOptionAllowList(WebhookEvent webhookEv, string eventName, WebhookTypes.EventSpec spec)
    {
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.Types is not null && webhookEv.Types.Length > 0, "types"u8, webhookEv.Types is not null && webhookEv.Types.Length > 0 ? Arena.GetStringRange(webhookEv.Types[0]) : null);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.Branches is not null, "branches"u8, webhookEv.Branches is not null ? Arena.GetStringRange(webhookEv.Branches.Name) : null);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.BranchesIgnore is not null, "branches-ignore"u8, webhookEv.BranchesIgnore is not null ? Arena.GetStringRange(webhookEv.BranchesIgnore.Name) : null);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.Tags is not null, "tags"u8, webhookEv.Tags is not null ? Arena.GetStringRange(webhookEv.Tags.Name) : null);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.TagsIgnore is not null, "tags-ignore"u8, webhookEv.TagsIgnore is not null ? Arena.GetStringRange(webhookEv.TagsIgnore.Name) : null);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.Paths is not null, "paths"u8, webhookEv.Paths is not null ? Arena.GetStringRange(webhookEv.Paths.Name) : null);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.PathsIgnore is not null, "paths-ignore"u8, webhookEv.PathsIgnore is not null ? Arena.GetStringRange(webhookEv.PathsIgnore.Name) : null);
        ValidateOptionAllowList(webhookEv, eventName, spec, webhookEv.Workflows is not null && webhookEv.Workflows.Length > 0, "workflows"u8, webhookEv.Workflows is not null && webhookEv.Workflows.Length > 0 ? Arena.GetStringRange(webhookEv.Workflows[0]) : null);
    }

    private void ValidateOptionAllowList(
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

    private void ValidateTypeValues(WebhookEvent webhookEv, string eventName, WebhookTypes.EventSpec spec)
    {
        if (webhookEv.Types is null || webhookEv.Types.Length == 0)
        {
            return;
        }

        if (!spec.IsTypeOptionSupported())
        {
            AddEventError(
                webhookEv,
                $"event '{eventName}' does not support 'types'",
                Arena.GetStringRange(webhookEv.Types[0]));
            return;
        }

        for (var i = 0; i < webhookEv.Types.Length; i++)
        {
            var typeNode = webhookEv.Types[i];
            var typeValue = Arena.GetStringValue(typeNode);
            if (Arena.GetStringExpression(typeNode).HasValue || typeValue.IndexOf("${{"u8) >= 0)
            {
                continue;
            }

            if (spec.IsTypeAllowed(typeValue))
            {
                continue;
            }

            var typeText = Decode(Arena.GetStringSlice(typeNode));
            AddEventError(
                webhookEv,
                $"event '{eventName}' has unsupported activity type '{typeText}'",
                Arena.GetStringRange(typeNode));
        }
    }

    private void ValidateMutualExclusionFilters(WebhookEvent webhookEv)
    {
        if (webhookEv.Branches is not null && webhookEv.BranchesIgnore is not null)
        {
            AddEventError(
                webhookEv,
                "event filter 'branches' and 'branches-ignore' cannot be used together",
                Arena.GetStringRange(webhookEv.BranchesIgnore.Name));
        }

        if (webhookEv.Tags is not null && webhookEv.TagsIgnore is not null)
        {
            AddEventError(
                webhookEv,
                "event filter 'tags' and 'tags-ignore' cannot be used together",
                Arena.GetStringRange(webhookEv.TagsIgnore.Name));
        }

        if (webhookEv.Paths is not null && webhookEv.PathsIgnore is not null)
        {
            AddEventError(
                webhookEv,
                "event filter 'paths' and 'paths-ignore' cannot be used together",
                Arena.GetStringRange(webhookEv.PathsIgnore.Name));
        }
    }

    private void ValidateFilter(WebhookEvent webhookEv, WebhookEventFilter? filter)
    {
        if (filter is null)
        {
            return;
        }

        var filterName = Decode(Arena.GetStringSlice(filter.Name));
        for (var i = 0; i < filter.Values.Length; i++)
        {
            var valueNode = filter.Values[i];
            var pattern = Arena.GetStringValue(valueNode);
            if (Arena.GetStringExpression(valueNode).HasValue || pattern.IndexOf("${{"u8) >= 0)
            {
                continue;
            }

            if (TryGetInvalidReason(pattern, out var reason))
            {
                var patternText = Decode(Arena.GetStringSlice(valueNode));
                AddEventError(
                    webhookEv,
                    $"event filter '{filterName}' has invalid glob pattern '{patternText}': {reason}",
                    Arena.GetStringRange(valueNode));
            }
        }
    }

    private static bool TryGetInvalidReason(ReadOnlySpan<byte> pattern, out string reason)
    {
        var consecutiveStars = 0;
        var openBracketCount = 0;
        var insideBracket = false;

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
                // Check for special character followed by + (e.g. *+, **+, ?+)
                if (b == (byte)'+' && consecutiveStars > 0)
                {
                    reason = "unexpected character '+' after '*'. the preceding character must not be a special character";
                    return true;
                }

                consecutiveStars = 0;
            }

            if (b == (byte)'[')
            {
                openBracketCount++;
                insideBracket = true;
            }
            else if (b == (byte)']')
            {
                if (openBracketCount == 0)
                {
                    reason = "closing ']' without opening '['";
                    return true;
                }

                openBracketCount--;
                insideBracket = false;
            }

            // Detect reversed ranges inside brackets: [z-a]
            if (insideBracket && b == (byte)'-' && i >= 2 && i + 1 < pattern.Length)
            {
                var prev = pattern[i - 1];
                var next = pattern[i + 1];
                if (prev != (byte)'[' && next != (byte)']' && prev > next)
                {
                    reason = $"reversed range '[{(char)prev}-{(char)next}]' in character class";
                    return true;
                }
            }

            // Detect git-check-ref-format violation characters (outside brackets)
            if (!insideBracket && IsRefNameForbiddenChar(b))
            {
                reason = $"character '{(char)b}' is invalid for branch and tag names. ref name cannot contain spaces, ~, ^, :, ?, backslash";
                return true;
            }
        }

        if (openBracketCount > 0)
        {
            reason = "'[' is not closed";
            return true;
        }

        // Detect '.' or '..' path segments
        if (ContainsDotSegment(pattern))
        {
            reason = "'.' and '..' are not allowed in path";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsRefNameForbiddenChar(byte b)
    {
        return b == (byte)'^'
            || b == (byte)'~'
            || b == (byte)':'
            || b == (byte)' ';
    }

    private static bool ContainsDotSegment(ReadOnlySpan<byte> pattern)
    {
        // Check for '.' or '..' as a path segment:
        // Bare "." or "..", or leading "./" "../", or "/./" "/../", or trailing "/." "/.."
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != (byte)'.')
            {
                continue;
            }

            var atStart = i == 0 || pattern[i - 1] == (byte)'/' || pattern[i - 1] == (byte)'\\';
            if (!atStart)
            {
                continue;
            }

            // Single dot segment: "." at end, or "./" or ".\"
            if (i + 1 >= pattern.Length || pattern[i + 1] == (byte)'/' || pattern[i + 1] == (byte)'\\')
            {
                return true;
            }

            // Double dot segment: ".." at end, or "../" or "..\"
            if (pattern[i + 1] == (byte)'.')
            {
                if (i + 2 >= pattern.Length || pattern[i + 2] == (byte)'/' || pattern[i + 2] == (byte)'\\')
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string DecodeAscii(ReadOnlySpan<byte> utf8)
    {
        var chars = new char[utf8.Length];
        for (var i = 0; i < utf8.Length; i++)
        {
            chars[i] = (char)utf8[i];
        }

        return new string(chars);
    }
}
