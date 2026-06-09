namespace Seiton.Core.Linting.Fixing;

internal static class Utf8YamlLineHelpers
{
    internal static bool ByteLineHasKeyAtIndent(byte[] utf8Yaml, int lineStart, int lineEnd, string indent, ReadOnlySpan<byte> keyBytes)
    {
        if (lineEnd - lineStart < indent.Length)
        {
            return false;
        }

        for (var k = 0; k < indent.Length; k++)
        {
            if (utf8Yaml[lineStart + k] != (byte)indent[k])
            {
                return false;
            }
        }

        var idx = lineStart + indent.Length;
        while (idx < lineEnd && (utf8Yaml[idx] == (byte)' ' || utf8Yaml[idx] == (byte)'\t'))
        {
            idx++;
        }

        var remaining = lineEnd - idx;
        if (remaining < keyBytes.Length)
        {
            return false;
        }

        return utf8Yaml.AsSpan(idx, keyBytes.Length).SequenceEqual(keyBytes);
    }

    internal static int FindLineWithKey(byte[] utf8Yaml, int startLine, int endLine, string indent, ReadOnlySpan<byte> keyPrefix)
    {
        var currentLine = 1;
        var pos = 0;
        while (currentLine < startLine && pos < utf8Yaml.Length)
        {
            if (utf8Yaml[pos++] == (byte)'\n')
            {
                currentLine++;
            }
        }

        while (currentLine <= endLine && pos <= utf8Yaml.Length)
        {
            if (pos >= utf8Yaml.Length)
            {
                break;
            }

            var lineStart = pos;
            while (pos < utf8Yaml.Length && utf8Yaml[pos] != (byte)'\n')
            {
                pos++;
            }

            var lineEnd = pos;
            if (lineEnd > lineStart && utf8Yaml[lineEnd - 1] == (byte)'\r')
            {
                lineEnd--;
            }

            if (pos < utf8Yaml.Length)
            {
                pos++;
            }

            if (ByteLineHasKeyAtIndent(utf8Yaml, lineStart, lineEnd, indent, keyPrefix))
            {
                return currentLine;
            }

            currentLine++;
        }

        return -1;
    }

    internal static int FindLineStartOffset(byte[] utf8Yaml, int lineNumber)
    {
        if (lineNumber <= 1)
        {
            return 0;
        }

        var currentLine = 1;
        for (var i = 0; i < utf8Yaml.Length; i++)
        {
            if (utf8Yaml[i] != (byte)'\n')
            {
                continue;
            }

            currentLine++;
            if (currentLine == lineNumber)
            {
                return i + 1;
            }
        }

        return utf8Yaml.Length;
    }

    internal static int FindLineEndOffsetIncludingNewLine(byte[] utf8Yaml, int lineNumber)
    {
        var start = FindLineStartOffset(utf8Yaml, lineNumber);
        for (var i = start; i < utf8Yaml.Length; i++)
        {
            if (utf8Yaml[i] == (byte)'\n')
            {
                return i + 1;
            }
        }

        return utf8Yaml.Length;
    }

    internal static int FindLineNumberFromOffset(byte[] utf8Yaml, int offset)
    {
        if (offset <= 0)
        {
            return 1;
        }

        if (offset > utf8Yaml.Length)
        {
            offset = utf8Yaml.Length;
        }

        var lineNumber = 1;
        for (var i = 0; i < offset; i++)
        {
            if (utf8Yaml[i] == (byte)'\n')
            {
                lineNumber++;
            }
        }

        return lineNumber;
    }
}
