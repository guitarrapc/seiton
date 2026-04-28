using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Linting.Rules;

/// <summary>Validates glob patterns in branch/path/tag filters for syntax errors.</summary>
public sealed class GlobPatternRule() : RuleBase(RuleId.GlobPattern)
{
    private const string FilterPatternNote = ". note: filter pattern syntax is explained at https://docs.github.com/en/actions/using-workflows/workflow-syntax-for-github-actions#filter-pattern-cheat-sheet";
    private const string RefFormatNote = ". see `man git-check-ref-format` for more details. note that regular expression is unavailable";

    public override string Name => "Glob Pattern Rule";

    public override void VisitEvent(Event ev)
    {
        if (Config.Utf8Yaml is null)
        {
            return;
        }

        if (ev is WebhookEvent webhookEv)
        {
            // Note: Option allow-list, activity type, and mutual exclusion checks are handled
            // at parser level (WorkflowParser.On.Webhook.cs). This rule only validates glob syntax.
            Action<string, TextRange> reportError = (msg, range) => AddEventError(webhookEv, msg, range);

            ValidateFilter(reportError, webhookEv.Branches, FilterKind.Ref);
            ValidateFilter(reportError, webhookEv.BranchesIgnore, FilterKind.Ref);
            ValidateFilter(reportError, webhookEv.Tags, FilterKind.Ref);
            ValidateFilter(reportError, webhookEv.TagsIgnore, FilterKind.Ref);
            ValidateFilter(reportError, webhookEv.Paths, FilterKind.Path);
            ValidateFilter(reportError, webhookEv.PathsIgnore, FilterKind.Path);
        }
        else if (ev is ImageVersionEvent imageVersionEv)
        {
            Action<string, TextRange> reportError = (msg, range) => AddEventError(imageVersionEv, msg, range);
            ValidateValues(reportError, imageVersionEv.Versions, FilterKind.Version);
        }
    }

    public override void VisitJobPre(Job job)
    {
        if (Config.Utf8Yaml is null || job.Snapshot is null)
        {
            return;
        }

        var version = job.Snapshot.Version;
        if (!version.HasValue)
        {
            return;
        }

        var pattern = Arena.GetStringValue(version);
        if (ExpressionScanHelpers.ContainsExpressionMarker(version, Arena))
        {
            return;
        }

        Action<string, TextRange> reportError = (msg, range) => AddJobError(job, msg, range);
        ValidatePattern(reportError, version, pattern, FilterKind.Version);
    }

    private enum FilterKind { Ref, Path, Version }

    private void ValidateFilter(Action<string, TextRange> reportError, WebhookEventFilter? filter, FilterKind kind)
    {
        if (filter is null)
        {
            return;
        }

        ValidateValues(reportError, filter.Values, kind);
    }

    private void ValidateValues(Action<string, TextRange> reportError, StringNodeId[]? values, FilterKind kind)
    {
        if (values is null)
        {
            return;
        }

        for (var i = 0; i < values.Length; i++)
        {
            var valueNode = values[i];
            var pattern = Arena.GetStringValue(valueNode);
            if (ExpressionScanHelpers.ContainsExpressionMarker(valueNode, Arena))
            {
                continue;
            }

            ValidatePattern(reportError, valueNode, pattern, kind);
        }
    }

    private void ValidatePattern(Action<string, TextRange> reportError, StringNodeId valueNode, ReadOnlySpan<byte> pattern, FilterKind kind)
    {
        var range = Arena.GetStringRange(valueNode);

        // Empty pattern
        if (pattern.Length == 0)
        {
            reportError("glob pattern should not be empty", range);
            return;
        }

        // Lone '!' — negate pattern must have at least one character following
        if (pattern.Length == 1 && pattern[0] == (byte)'!')
        {
            reportError(
                $"invalid glob pattern. unexpected character '!' while checking ! at first character (negate pattern). at least one character must follow !{FilterPatternNote}",
                range);
            return;
        }

        // Leading/trailing spaces (path filters)
        if (kind == FilterKind.Path && (IsGlobWhitespace(pattern[0]) || IsGlobWhitespace(pattern[^1])))
        {
            reportError(
                $"leading and trailing spaces are not allowed in glob path{FilterPatternNote}",
                range);
            return;
        }

        // Ref name validations
        if (kind == FilterKind.Ref)
        {
            if (pattern[0] == (byte)'/')
            {
                reportError(
                    $"character '/' is invalid for branch and tag names. ref name must not start with /{RefFormatNote}{FilterPatternNote}",
                    range);
            }

            if (pattern[^1] == (byte)'/')
            {
                reportError(
                    $"character '/' is invalid for branch and tag names. ref name must not end with / and ..{RefFormatNote}{FilterPatternNote}",
                    range);
            }
        }

        var consecutiveStars = 0;
        var openBracketCount = 0;
        var insideBracket = false;
        var bracketStart = -1;

        for (var i = 0; i < pattern.Length; i++)
        {
            var b = pattern[i];
            if (b == (byte)'*')
            {
                consecutiveStars++;
                if (consecutiveStars >= 3)
                {
                    reportError(
                        $"invalid glob pattern. consecutive '*' longer than '**' is not supported{FilterPatternNote}",
                        range);
                    return;
                }
            }
            else
            {
                if (b == (byte)'+' && consecutiveStars > 0)
                {
                    reportError(
                        $"invalid glob pattern. unexpected character '+' after '*'. the preceding character must not be a special character{FilterPatternNote}",
                        range);
                    return;
                }

                consecutiveStars = 0;
            }

            if (b == (byte)'[')
            {
                openBracketCount++;
                insideBracket = true;
                bracketStart = i;
            }
            else if (b == (byte)']')
            {
                if (insideBracket)
                {
                    // Check single-character class: [x] where x is a single non-special char
                    var bracketLen = i - bracketStart - 1;
                    if (bracketLen == 1)
                    {
                        var inner = pattern[bracketStart + 1];
                        reportError(
                            $"invalid glob pattern. unexpected character ']' while checking character match []. character match with single character is useless. simply use {(char)inner} instead of [{(char)inner}]{FilterPatternNote}",
                            range);
                    }

                    openBracketCount--;
                    insideBracket = false;
                }
                else if (openBracketCount == 0)
                {
                    reportError(
                        $"invalid glob pattern. closing ']' without opening '['{FilterPatternNote}",
                        range);
                    return;
                }
            }

            // Detect reversed ranges inside brackets: [z-a]
            if (insideBracket && b == (byte)'-' && i >= 2 && i + 1 < pattern.Length)
            {
                var prev = pattern[i - 1];
                var next = pattern[i + 1];
                if (prev != (byte)'[' && next != (byte)']' && prev > next)
                {
                    reportError(
                        $"invalid glob pattern. unexpected character '{(char)next}' while checking character range in []. start of range '{(char)prev}' ({(int)prev}) is larger than end of range '{(char)next}' ({(int)next}){FilterPatternNote}",
                        range);
                    return;
                }
            }

            // Validate backslash escapes
            if (b == (byte)'\\' && !insideBracket)
            {
                if (i + 1 >= pattern.Length)
                {
                    reportError(
                        $"invalid glob pattern. trailing backslash '\\' with no character to escape{FilterPatternNote}",
                        range);
                    return;
                }

                var next = pattern[i + 1];
                if (!IsGlobEscapable(next))
                {
                    if (kind == FilterKind.Ref)
                    {
                        reportError(
                            $"character '\\' is invalid for branch and tag names. only special characters [, ?, +, *, \\, ! can be escaped with \\{RefFormatNote}{FilterPatternNote}",
                            range);
                    }
                    else
                    {
                        reportError(
                            $"invalid glob pattern. '\\{(char)next}' is not a valid glob escape; only glob metacharacters (*, ?, [, ], \\, !, +, #) can be escaped{FilterPatternNote}",
                            range);
                    }
                }

                i++; // skip escaped character
                continue;
            }

            // Detect git-check-ref-format violation characters (outside brackets)
            if (kind == FilterKind.Ref && !insideBracket && IsRefNameForbiddenChar(b))
            {
                reportError(
                    $"character '{(char)b}' is invalid for branch and tag names. ref name cannot contain spaces, ~, ^, :, [, ?, *{RefFormatNote}{FilterPatternNote}",
                    range);
            }
        }

        if (openBracketCount > 0)
        {
            reportError(
                $"invalid glob pattern. unexpected EOF while checking end of character match []. missing ]{FilterPatternNote}",
                range);
            return;
        }

        // Detect '.' or '..' path segments
        if (ContainsDotSegment(pattern))
        {
            reportError(
                $"'.' and '..' are not allowed in glob path{FilterPatternNote}",
                range);
        }
    }

    private static bool IsRefNameForbiddenChar(byte b)
    {
        return b == (byte)'^'
            || b == (byte)'~'
            || b == (byte)':'
            || b == (byte)' ';
    }

    private static bool IsGlobWhitespace(byte b)
    {
        return b == (byte)' '
            || b == (byte)'\n'
            || b == (byte)'\r'
            || b == (byte)'\t';
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

            if (i + 1 >= pattern.Length || pattern[i + 1] == (byte)'/' || pattern[i + 1] == (byte)'\\')
            {
                return true;
            }

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
}
