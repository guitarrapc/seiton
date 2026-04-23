using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Linting.Fixing;

public enum ScalarQuoteStyle
{
    Unquoted,
    SingleQuoted,
    DoubleQuoted,
}

/// <summary>
/// Utilities for YAML-aware text formatting: line ending detection, indentation measurement,
/// scalar quoting, and comment construction used when building auto-fix edits.
/// </summary>
public static class FixFormatting
{
    /// <summary>Detects the dominant line ending (CRLF or LF) in the given UTF-8 YAML bytes.</summary>
    public static string DetectDominantLineEnding(byte[] utf8Yaml)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);

        var crlfCount = 0;
        var lfCount = 0;
        for (var i = 0; i < utf8Yaml.Length; i++)
        {
            if (utf8Yaml[i] != (byte)'\n')
            {
                continue;
            }

            if (i > 0 && utf8Yaml[i - 1] == (byte)'\r')
            {
                crlfCount++;
                continue;
            }

            lfCount++;
        }

        return crlfCount > lfCount ? "\r\n" : "\n";
    }

    /// <summary>Infers the indentation string for an insertion point, using a sibling or parent line as reference.</summary>
    public static string InferIndentation(string sourceText, int? siblingLineNumber, int parentLineNumber)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentOutOfRangeException.ThrowIfLessThan(parentLineNumber, 1);

        if (siblingLineNumber is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(siblingLineNumber.Value, 1);
            var siblingIndentation = GetLineIndentation(sourceText, siblingLineNumber.Value);
            if (siblingIndentation.Length > 0 || LineExists(sourceText, siblingLineNumber.Value))
            {
                return siblingIndentation;
            }
        }

        return GetLineIndentation(sourceText, parentLineNumber) + InferIndentationUnit(sourceText);
    }

    /// <summary>Tries to infer indentation within a scope, returning <c>false</c> if mixed indentation is detected.</summary>
    public static bool TryInferIndentation(
        string sourceText,
        int? siblingLineNumber,
        int parentLineNumber,
        int scopeStartLine,
        int scopeEndLine,
        out string indentation)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentOutOfRangeException.ThrowIfLessThan(parentLineNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(scopeStartLine, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(scopeEndLine, scopeStartLine);

        var parentIndentation = GetLineIndentation(sourceText, parentLineNumber);
        if (IsMixedIndentationInScope(sourceText, scopeStartLine, scopeEndLine, parentIndentation))
        {
            indentation = string.Empty;
            return false;
        }

        if (siblingLineNumber is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(siblingLineNumber.Value, 1);
            var siblingIndentation = GetLineIndentation(sourceText, siblingLineNumber.Value);
            if (siblingIndentation.Length > 0 || LineExists(sourceText, siblingLineNumber.Value))
            {
                indentation = siblingIndentation;
                return true;
            }
        }

        var indentationUnit = InferIndentationUnit(sourceText);
        if (IsSpaceOnlyIndentation(parentIndentation) && indentationUnit == "\t")
        {
            indentation = string.Empty;
            return false;
        }

        indentation = parentIndentation + indentationUnit;
        return true;
    }

    /// <summary>Returns the leading whitespace of the specified 1-based <paramref name="lineNumber"/>.</summary>
    public static string GetLineIndentation(string sourceText, int lineNumber)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentOutOfRangeException.ThrowIfLessThan(lineNumber, 1);

        var line = GetCharLine(sourceText, lineNumber);
        var count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
        {
            count++;
        }

        return count == 0 ? string.Empty : new string(line[..count]);
    }

    /// <summary>Returns the leading whitespace of the specified 1-based <paramref name="lineNumber"/> from UTF-8 bytes.</summary>
    public static string GetLineIndentation(byte[] utf8Yaml, int lineNumber)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentOutOfRangeException.ThrowIfLessThan(lineNumber, 1);

        var line = GetByteLine(utf8Yaml, lineNumber);
        var count = 0;
        while (count < line.Length && (line[count] == (byte)' ' || line[count] == (byte)'\t'))
        {
            count++;
        }

        return count == 0 ? string.Empty : Encoding.UTF8.GetString(line[..count]);
    }

    /// <summary>Infers the smallest indentation unit (spaces or tab) used in the source text.</summary>
    public static string InferIndentationUnit(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        var text = sourceText.AsSpan();
        var bestSpaceCount = 0;
        var pos = 0;
        while (pos < text.Length)
        {
            var lineStart = pos;
            while (pos < text.Length && text[pos] != '\n') pos++;
            var lineEnd = pos;
            if (lineEnd > lineStart && text[lineEnd - 1] == '\r') lineEnd--;
            if (pos < text.Length) pos++;

            var line = text[lineStart..lineEnd];
            if (line.IsEmpty) continue;

            var allWs = true;
            for (var k = 0; k < line.Length; k++)
            {
                if (line[k] != ' ' && line[k] != '\t') { allWs = false; break; }
            }
            if (allWs) continue;

            var spaceCount = 0;
            while (spaceCount < line.Length && line[spaceCount] == ' ') spaceCount++;

            if (spaceCount > 0 && (bestSpaceCount == 0 || spaceCount < bestSpaceCount))
                bestSpaceCount = spaceCount;

            if (spaceCount < line.Length && line[spaceCount] == '\t')
                return "\t";
        }

        return bestSpaceCount > 0 ? new string(' ', bestSpaceCount) : "  ";
    }

    /// <summary>Infers the smallest indentation unit (spaces or tab) used in UTF-8 YAML bytes.</summary>
    public static string InferIndentationUnit(byte[] utf8Yaml)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);

        var bestSpaceCount = 0;
        var pos = 0;
        while (pos < utf8Yaml.Length)
        {
            var lineStart = pos;
            while (pos < utf8Yaml.Length && utf8Yaml[pos] != (byte)'\n') pos++;
            var lineEnd = pos;
            if (lineEnd > lineStart && utf8Yaml[lineEnd - 1] == (byte)'\r') lineEnd--;
            if (pos < utf8Yaml.Length) pos++;

            if (lineEnd == lineStart) continue;

            var allWs = true;
            for (var k = lineStart; k < lineEnd; k++)
            {
                if (utf8Yaml[k] != (byte)' ' && utf8Yaml[k] != (byte)'\t') { allWs = false; break; }
            }
            if (allWs) continue;

            var spaceCount = 0;
            while (lineStart + spaceCount < lineEnd && utf8Yaml[lineStart + spaceCount] == (byte)' ') spaceCount++;

            if (spaceCount > 0 && (bestSpaceCount == 0 || spaceCount < bestSpaceCount))
                bestSpaceCount = spaceCount;

            if (lineStart + spaceCount < lineEnd && utf8Yaml[lineStart + spaceCount] == (byte)'\t')
                return "\t";
        }

        return bestSpaceCount > 0 ? new string(' ', bestSpaceCount) : "  ";
    }

    /// <summary>Detects the quote style (unquoted, single-quoted, or double-quoted) of a YAML scalar at the given range.</summary>
    public static ScalarQuoteStyle DetectQuoteStyle(byte[] utf8Yaml, TextRange range, bool quoted)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);

        if (!quoted)
        {
            return ScalarQuoteStyle.Unquoted;
        }

        if (range.Start >= 0 && range.Start < utf8Yaml.Length)
        {
            if (utf8Yaml[range.Start] == (byte)'\'')
            {
                return ScalarQuoteStyle.SingleQuoted;
            }

            if (utf8Yaml[range.Start] == (byte)'"')
            {
                return ScalarQuoteStyle.DoubleQuoted;
            }
        }

        if (range.Start > 0 && range.Start - 1 < utf8Yaml.Length)
        {
            if (utf8Yaml[range.Start - 1] == (byte)'\'')
            {
                return ScalarQuoteStyle.SingleQuoted;
            }

            if (utf8Yaml[range.Start - 1] == (byte)'"')
            {
                return ScalarQuoteStyle.DoubleQuoted;
            }
        }

        return ScalarQuoteStyle.DoubleQuoted;
    }

    private static bool LineExists(string sourceText, int lineNumber)
    {
        if (lineNumber <= 1) return sourceText.Length > 0;
        var lineCount = 1;
        var text = sourceText.AsSpan();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lineCount++;
                if (lineCount >= lineNumber) return true;
            }
        }
        return false;
    }

    private static bool IsMixedIndentationInScope(string sourceText, int scopeStartLine, int scopeEndLine, string parentIndentation)
    {
        var text = sourceText.AsSpan();
        var pIndent = parentIndentation.AsSpan();
        var sawSpaceIndentedChild = false;
        var sawTabIndentedChild = false;

        var currentLine = 1;
        var pos = 0;
        while (currentLine < scopeStartLine && pos < text.Length)
            if (text[pos++] == '\n') currentLine++;

        while (currentLine <= scopeEndLine && pos <= text.Length)
        {
            if (pos >= text.Length) break;
            var lineStart = pos;
            while (pos < text.Length && text[pos] != '\n') pos++;
            var lineEnd = pos;
            if (lineEnd > lineStart && text[lineEnd - 1] == '\r') lineEnd--;
            if (pos < text.Length) pos++;

            var line = text[lineStart..lineEnd];
            var trimmed = line.TrimStart();
            if (trimmed.IsEmpty || trimmed[0] == '#') { currentLine++; continue; }

            if (!line.StartsWith(pIndent)) { currentLine++; continue; }

            var tail = line[pIndent.Length..];
            if (tail.IsEmpty) { currentLine++; continue; }

            if (tail[0] == ' ') sawSpaceIndentedChild = true;
            else if (tail[0] == '\t') sawTabIndentedChild = true;

            if (sawSpaceIndentedChild && sawTabIndentedChild) return true;
            currentLine++;
        }

        return false;
    }

    private static bool IsMixedIndentationInScope(byte[] utf8Yaml, int scopeStartLine, int scopeEndLine, string parentIndentation)
    {
        var sawSpaceIndentedChild = false;
        var sawTabIndentedChild = false;

        var currentLine = 1;
        var pos = 0;
        while (currentLine < scopeStartLine && pos < utf8Yaml.Length)
            if (utf8Yaml[pos++] == (byte)'\n') currentLine++;

        while (currentLine <= scopeEndLine && pos <= utf8Yaml.Length)
        {
            if (pos >= utf8Yaml.Length) break;
            var lineStart = pos;
            while (pos < utf8Yaml.Length && utf8Yaml[pos] != (byte)'\n') pos++;
            var lineEnd = pos;
            if (lineEnd > lineStart && utf8Yaml[lineEnd - 1] == (byte)'\r') lineEnd--;
            if (pos < utf8Yaml.Length) pos++;

            var lineLen = lineEnd - lineStart;
            if (lineLen == 0) { currentLine++; continue; }

            // skip whitespace-only and comment lines
            var firstNonWs = lineStart;
            while (firstNonWs < lineEnd && (utf8Yaml[firstNonWs] == (byte)' ' || utf8Yaml[firstNonWs] == (byte)'\t')) firstNonWs++;
            if (firstNonWs >= lineEnd || utf8Yaml[firstNonWs] == (byte)'#') { currentLine++; continue; }

            // check parentIndentation prefix
            if (!ByteLineStartsWithString(utf8Yaml, lineStart, lineEnd, parentIndentation)) { currentLine++; continue; }

            var tailStart = lineStart + parentIndentation.Length;
            if (tailStart >= lineEnd) { currentLine++; continue; }

            if (utf8Yaml[tailStart] == (byte)' ') sawSpaceIndentedChild = true;
            else if (utf8Yaml[tailStart] == (byte)'\t') sawTabIndentedChild = true;

            if (sawSpaceIndentedChild && sawTabIndentedChild) return true;
            currentLine++;
        }

        return false;
    }

    private static bool IsSpaceOnlyIndentation(string indentation)
    {
        if (indentation.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < indentation.Length; i++)
        {
            if (indentation[i] != ' ')
            {
                return false;
            }
        }

        return true;
    }

    // Returns a span of line N content (1-based), excluding the newline. No allocation.
    private static ReadOnlySpan<char> GetCharLine(string text, int lineNumber)
    {
        var span = text.AsSpan();
        var currentLine = 1;
        var pos = 0;
        while (currentLine < lineNumber && pos < span.Length)
            if (span[pos++] == '\n') currentLine++;
        if (currentLine < lineNumber) return ReadOnlySpan<char>.Empty;
        var lineStart = pos;
        while (pos < span.Length && span[pos] != '\n') pos++;
        var lineEnd = pos;
        if (lineEnd > lineStart && span[lineEnd - 1] == '\r') lineEnd--;
        return span[lineStart..lineEnd];
    }

    // Returns a span of byte line N content (1-based), excluding the newline. No allocation.
    private static ReadOnlySpan<byte> GetByteLine(byte[] utf8Yaml, int lineNumber)
    {
        var currentLine = 1;
        var pos = 0;
        while (currentLine < lineNumber && pos < utf8Yaml.Length)
            if (utf8Yaml[pos++] == (byte)'\n') currentLine++;
        if (currentLine < lineNumber) return ReadOnlySpan<byte>.Empty;
        var lineStart = pos;
        while (pos < utf8Yaml.Length && utf8Yaml[pos] != (byte)'\n') pos++;
        var lineEnd = pos;
        if (lineEnd > lineStart && utf8Yaml[lineEnd - 1] == (byte)'\r') lineEnd--;
        return utf8Yaml.AsSpan(lineStart, lineEnd - lineStart);
    }

    // Returns true when bytes in [lineStart..lineEnd) start with the ASCII string prefix.
    private static bool ByteLineStartsWithString(byte[] utf8Yaml, int lineStart, int lineEnd, string prefix)
    {
        if (lineEnd - lineStart < prefix.Length) return false;
        for (var k = 0; k < prefix.Length; k++)
            if (utf8Yaml[lineStart + k] != (byte)prefix[k]) return false;
        return true;
    }

    /// <summary>Tries to infer indentation within a scope from UTF-8 bytes, returning <c>false</c> if mixed indentation is detected.</summary>
    public static bool TryInferIndentation(
        byte[] utf8Yaml,
        int? siblingLineNumber,
        int parentLineNumber,
        int scopeStartLine,
        int scopeEndLine,
        out string indentation)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentOutOfRangeException.ThrowIfLessThan(parentLineNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(scopeStartLine, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(scopeEndLine, scopeStartLine);

        var parentIndentation = GetLineIndentation(utf8Yaml, parentLineNumber);
        if (IsMixedIndentationInScope(utf8Yaml, scopeStartLine, scopeEndLine, parentIndentation))
        {
            indentation = string.Empty;
            return false;
        }

        if (siblingLineNumber is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(siblingLineNumber.Value, 1);
            var siblingIndentation = GetLineIndentation(utf8Yaml, siblingLineNumber.Value);
            if (siblingIndentation.Length > 0 || ByteLineExists(utf8Yaml, siblingLineNumber.Value))
            {
                indentation = siblingIndentation;
                return true;
            }
        }

        var indentationUnit = InferIndentationUnit(utf8Yaml);
        if (IsSpaceOnlyIndentation(parentIndentation) && indentationUnit == "\t")
        {
            indentation = string.Empty;
            return false;
        }

        indentation = parentIndentation + indentationUnit;
        return true;
    }

    private static bool ByteLineExists(byte[] utf8Yaml, int lineNumber)
    {
        if (lineNumber <= 1) return utf8Yaml.Length > 0;
        var lineCount = 1;
        for (var i = 0; i < utf8Yaml.Length; i++)
        {
            if (utf8Yaml[i] == (byte)'\n')
            {
                lineCount++;
                if (lineCount >= lineNumber) return true;
            }
        }
        return false;
    }
}
