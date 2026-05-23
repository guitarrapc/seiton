using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    /// <summary>
    /// After a fatal YAML parse, heuristically checks whether the failure is caused by
    /// a plain scalar in <c>run:</c> or <c>script:</c> containing <c>: </c> (colon-space).
    /// Returns an explanatory help message if the pattern matches, <c>null</c> otherwise.
    /// </summary>
    /// <remarks>
    /// This runs only on the error path (post-fatal-parse), so allocation of the returned
    /// string is acceptable. The heuristic inspects the error line and a few lines above
    /// to detect the <c>run:</c>/<c>script:</c> + plain scalar + colon-space pattern.
    /// </remarks>
    internal static string? TryGetPlainScalarColonHint(ReadOnlySpan<byte> source, int errorOffset, int errorLine)
    {
        if (source.IsEmpty || errorOffset < 0)
            return null;

        // Clamp offset to valid range
        var offset = Math.Min(errorOffset, source.Length - 1);

        // Scan back up to 3 lines from the error position to find a candidate `run:` or `script:` line.
        // The error position from VYaml can be on the same line as the key or 1-2 lines after.
        const int maxLinesToScan = 4;
        var linesChecked = 0;
        var searchStart = offset;

        while (linesChecked < maxLinesToScan && searchStart >= 0)
        {
            var lineStart = FindLineStart(source, searchStart);
            var lineEnd = FindLineEnd(source, lineStart);
            var line = source[lineStart..lineEnd];

            var matchedKey = TryMatchRunOrScriptKey(line);
            if (matchedKey != null)
            {
                // Found a run:/script: key on this line — check if value is a plain scalar with `: `
                var keyEndOffset = GetKeyEndOffset(line, matchedKey);
                if (keyEndOffset >= 0 && keyEndOffset < line.Length)
                {
                    var valueStart = SkipSpaces(line, keyEndOffset);
                    valueStart = SkipYamlNodeProperties(line, valueStart);
                    if (valueStart < line.Length && IsPlainScalarStart(line[valueStart]))
                    {
                        // Check if the value portion contains `: ` (colon + space)
                        var valueEnd = FindInlineCommentStart(line, valueStart);
                        var valuePortion = line[valueStart..valueEnd];
                        if (ContainsColonSpace(valuePortion))
                        {
                            return matchedKey.Length == 3
                                ? "the plain scalar value after 'run:' contains ': ' which is invalid in YAML. Quote the value or use a block scalar (|) instead"
                                : "the plain scalar value after 'script:' contains ': ' which is invalid in YAML. Quote the value or use a block scalar (|) instead";
                        }
                    }
                }
            }

            // Move to previous line
            linesChecked++;
            searchStart = lineStart - 1;
        }

        return null;
    }

    /// <summary>
    /// Finds the start of the line containing the given offset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindLineStart(ReadOnlySpan<byte> source, int offset)
    {
        if (offset > 0 && source[offset] == (byte)'\n')
            offset--;

        for (var i = offset; i >= 0; i--)
        {
            if (source[i] == (byte)'\n')
                return i + 1;
        }
        return 0;
    }

    /// <summary>
    /// Finds the end of the line starting at the given offset (exclusive, points to \n or end of source).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindLineEnd(ReadOnlySpan<byte> source, int start)
    {
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == (byte)'\n')
                return i;
        }
        return source.Length;
    }

    /// <summary>
    /// Checks if a line contains <c>run:</c> or <c>script:</c> as a YAML key (preceded by whitespace or <c>- </c>).
    /// Returns the matched key name ("run" or "script") if found, null otherwise.
    /// </summary>
    private static string? TryMatchRunOrScriptKey(ReadOnlySpan<byte> line)
    {
        // Skip leading whitespace and optional `- `
        var i = 0;
        while (i < line.Length && (line[i] == (byte)' ' || line[i] == (byte)'\t'))
            i++;

        if (i < line.Length && line[i] == (byte)'-')
        {
            i++;
            while (i < line.Length && line[i] == (byte)' ')
                i++;
        }

        var remaining = line[i..];

        // Check for `run:` (4 bytes)
        if (remaining.Length >= 4 && remaining[..4].SequenceEqual("run:"u8))
            return "run";

        // Check for `script:` (7 bytes)
        if (remaining.Length >= 7 && remaining[..7].SequenceEqual("script:"u8))
            return "script";

        return null;
    }

    /// <summary>
    /// Returns the offset within the line just past the key's colon.
    /// For example, for "run:" returns the index after ':'.
    /// </summary>
    private static int GetKeyEndOffset(ReadOnlySpan<byte> line, string key)
    {
        // Find the key + ':' in the line
        var keyWithColon = key == "run" ? "run:"u8 : "script:"u8;
        var idx = line.IndexOf(keyWithColon);
        if (idx < 0) return -1;
        return idx + keyWithColon.Length;
    }

    /// <summary>
    /// Skips space characters from the given position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SkipSpaces(ReadOnlySpan<byte> span, int start)
    {
        var i = start;
        while (i < span.Length && span[i] == (byte)' ')
            i++;
        return i;
    }

    /// <summary>
    /// Skips YAML node properties (`&anchor`, `!tag`) that may appear before the actual scalar token.
    /// </summary>
    private static int SkipYamlNodeProperties(ReadOnlySpan<byte> span, int start)
    {
        var i = start;

        while (i < span.Length)
        {
            if (span[i] == (byte)'&' || span[i] == (byte)'!')
            {
                i = SkipNonWhitespaceToken(span, i + 1);
                i = SkipSpaces(span, i);
                continue;
            }

            break;
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SkipNonWhitespaceToken(ReadOnlySpan<byte> span, int start)
    {
        var i = start;
        while (i < span.Length && span[i] != (byte)' ' && span[i] != (byte)'\t' && span[i] != (byte)'\r' && span[i] != (byte)'\n')
            i++;
        return i;
    }

    /// <summary>
    /// Returns the start of an inline YAML comment (` # comment`) or the end of the line if none exists.
    /// </summary>
    private static int FindInlineCommentStart(ReadOnlySpan<byte> span, int start)
    {
        for (var i = start + 1; i < span.Length; i++)
        {
            if (span[i] == (byte)'#' && (span[i - 1] == (byte)' ' || span[i - 1] == (byte)'\t'))
                return i;
        }

        return span.Length;
    }

    /// <summary>
    /// Checks if a byte is a valid start for a YAML plain scalar (not a quoted or block indicator).
    /// Returns true if NOT one of: ' " | &gt;
    /// Also returns false for empty/newline which indicates no value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPlainScalarStart(byte b)
    {
        return b != (byte)'\'' && b != (byte)'"' && b != (byte)'|' && b != (byte)'>'
            && b != (byte)'#'
            && b != (byte)'\n' && b != (byte)'\r';
    }

    /// <summary>
    /// Checks if the span contains ": " (colon followed by space) — the YAML mapping value indicator
    /// that causes plain scalar parse failures.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsColonSpace(ReadOnlySpan<byte> span)
    {
        return span.IndexOf(": "u8) >= 0;
    }
}
