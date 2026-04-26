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

        // Note: Option allow-list, activity type, and mutual exclusion checks are handled
        // at parser level (WorkflowParser.On.Webhook.cs). This rule only validates glob syntax.

        ValidateFilter(webhookEv, webhookEv.Branches, FilterKind.Ref);
        ValidateFilter(webhookEv, webhookEv.BranchesIgnore, FilterKind.Ref);
        ValidateFilter(webhookEv, webhookEv.Tags, FilterKind.Ref);
        ValidateFilter(webhookEv, webhookEv.TagsIgnore, FilterKind.Ref);
        ValidateFilter(webhookEv, webhookEv.Paths, FilterKind.Path);
        ValidateFilter(webhookEv, webhookEv.PathsIgnore, FilterKind.Path);
    }

    private enum FilterKind { Ref, Path }

    private void ValidateFilter(WebhookEvent webhookEv, WebhookEventFilter? filter, FilterKind kind)
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
            if (ExpressionScanHelpers.ContainsExpressionMarker(valueNode, Arena))
            {
                continue;
            }

            if (TryGetInvalidReason(pattern, kind, out var reason))
            {
                var patternText = Decode(Arena.GetStringSlice(valueNode));
                AddEventError(
                    webhookEv,
                    $"event filter '{filterName}' has invalid glob pattern '{patternText}': {reason}",
                    Arena.GetStringRange(valueNode));
            }
        }
    }

    private static bool TryGetInvalidReason(ReadOnlySpan<byte> pattern, FilterKind kind, out string reason)
    {
        // Empty pattern
        if (pattern.Length == 0)
        {
            reason = "pattern must not be empty";
            return true;
        }

        // Lone '!' — negate pattern must have at least one character following
        if (pattern.Length == 1 && pattern[0] == (byte)'!')
        {
            reason = "at least one character must follow '!' in negate pattern";
            return true;
        }

        // Leading/trailing spaces (for path filters)
        if (kind == FilterKind.Path)
        {
            if (pattern[0] == (byte)' ' || pattern[^1] == (byte)' ')
            {
                reason = "leading and trailing spaces are not allowed in glob path";
                return true;
            }
        }

        // Ref name: must not start with '/'
        if (kind == FilterKind.Ref && pattern[0] == (byte)'/')
        {
            reason = "ref name must not start with '/'";
            return true;
        }

        // Ref name: must not end with '/' or '..'
        if (kind == FilterKind.Ref && pattern[^1] == (byte)'/')
        {
            reason = "ref name must not end with '/'";
            return true;
        }

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

            // Validate backslash escapes: \X is valid only when X is a glob metacharacter
            if (b == (byte)'\\' && !insideBracket)
            {
                if (i + 1 >= pattern.Length)
                {
                    reason = "trailing backslash '\\' with no character to escape";
                    return true;
                }

                var next = pattern[i + 1];
                if (!IsGlobEscapable(next))
                {
                    reason = $"'\\{(char)next}' is not a valid glob escape; only glob metacharacters (*, ?, [, ], \\, !, +, #) can be escaped";
                    return true;
                }

                i++; // skip escaped character
                continue;
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

    private static bool IsGlobEscapable(byte b)
    {
        return b == (byte)'*'
            || b == (byte)'?'
            || b == (byte)'['
            || b == (byte)']'
            || b == (byte)'\\'
            || b == (byte)'!'
            || b == (byte)'+'
            || b == (byte)'#';
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
