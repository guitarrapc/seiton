using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.Fixing;

public enum ScalarQuoteStyle
{
    Unquoted,
    SingleQuoted,
    DoubleQuoted,
}

public static class FixFormatting
{
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

    public static string GetLineIndentation(string sourceText, int lineNumber)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentOutOfRangeException.ThrowIfLessThan(lineNumber, 1);

        var lines = SplitLines(sourceText);
        if (lineNumber > lines.Length)
        {
            return string.Empty;
        }

        var line = lines[lineNumber - 1];
        var count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
        {
            count++;
        }

        return line[..count];
    }

    public static string InferIndentationUnit(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        var lines = SplitLines(sourceText);
        string? bestSpaces = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indentationLength = 0;
            while (indentationLength < line.Length && line[indentationLength] == ' ')
            {
                indentationLength++;
            }

            if (indentationLength > 0)
            {
                var candidate = line[..indentationLength];
                if (bestSpaces is null || candidate.Length < bestSpaces.Length)
                {
                    bestSpaces = candidate;
                }
            }

            if (indentationLength < line.Length && line[indentationLength] == '\t')
            {
                return "\t";
            }
        }

        return bestSpaces ?? "  ";
    }

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

    static bool LineExists(string sourceText, int lineNumber)
    {
        var lines = SplitLines(sourceText);
        return lineNumber >= 1 && lineNumber <= lines.Length;
    }

    static bool IsMixedIndentationInScope(string sourceText, int scopeStartLine, int scopeEndLine, string parentIndentation)
    {
        var lines = SplitLines(sourceText);
        var maxLine = Math.Min(lines.Length, scopeEndLine);

        var sawSpaceIndentedChild = false;
        var sawTabIndentedChild = false;

        for (var lineNumber = scopeStartLine; lineNumber <= maxLine; lineNumber++)
        {
            var line = lines[lineNumber - 1];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.AsSpan().TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (!line.StartsWith(parentIndentation, StringComparison.Ordinal))
            {
                continue;
            }

            var tail = line[parentIndentation.Length..];
            if (tail.Length == 0)
            {
                continue;
            }

            if (tail[0] == ' ')
            {
                sawSpaceIndentedChild = true;
            }
            else if (tail[0] == '\t')
            {
                sawTabIndentedChild = true;
            }

            if (sawSpaceIndentedChild && sawTabIndentedChild)
            {
                return true;
            }
        }

        return false;
    }

    static bool IsSpaceOnlyIndentation(string indentation)
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

    static string[] SplitLines(string sourceText)
    {
        return sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }
}
