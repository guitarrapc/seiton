namespace Seiton.Core.Parsing;

public readonly record struct ExpressionOccurrence(
    Utf8Slice Slice,
    TextRange Location);

public static class ExpressionExtractor
{
    public static ExpressionOccurrence[] Extract(byte[] utf8Yaml)
    {
        var expressions = new List<ExpressionOccurrence>();
        var lineStarts = BuildLineStarts(utf8Yaml);

        var searchStart = 0;
        while (searchStart < utf8Yaml.Length - 3)
        {
            var start = IndexOf(utf8Yaml, searchStart, "${{"u8);
            if (start < 0)
            {
                break;
            }

            var bodyStart = start + 3;
            var end = IndexOf(utf8Yaml, bodyStart, "}}"u8);
            if (end < 0)
            {
                break;
            }

            var trimmed = TrimAsciiWhiteSpace(utf8Yaml, bodyStart, end - bodyStart);
            if (trimmed.Length > 0)
            {
                var startPos = OffsetToLineColumn(lineStarts, trimmed.Offset);
                var endPos = OffsetToLineColumn(lineStarts, trimmed.Offset + trimmed.Length - 1);
                var location = new TextRange(
                    Start: trimmed.Offset,
                    Length: trimmed.Length,
                    StartLine: startPos.Line,
                    StartColumn: startPos.Column,
                    EndLine: endPos.Line,
                    EndColumn: endPos.Column);

                expressions.Add(new ExpressionOccurrence(trimmed, location));
            }

            searchStart = end + 2;
        }

        return expressions.ToArray();
    }

    public static (ExpressionOccurrence[] Occurrences, Diagnostic[] Diagnostics) ExtractAndParse(byte[] utf8Yaml)
    {
        var occurrences = Extract(utf8Yaml);
        var diagnostics = new List<Diagnostic>();

        foreach (var occurrence in occurrences)
        {
            var expression = occurrence.Slice.AsSpan(utf8Yaml);
            var result = ExpressionParser.Parse(expression);
            for (var i = 0; i < result.Diagnostics.Length; i++)
            {
                diagnostics.Add(new Diagnostic(
                    result.Diagnostics[i].Severity,
                    $"expression parse error: {result.Diagnostics[i].Message}",
                    occurrence.Location));
            }
        }

        return (occurrences, diagnostics.ToArray());
    }

    private static Utf8Slice TrimAsciiWhiteSpace(byte[] source, int offset, int length)
    {
        var start = offset;
        var end = offset + length - 1;

        while (start <= end && IsWhiteSpace(source[start]))
        {
            start++;
        }

        while (end >= start && IsWhiteSpace(source[end]))
        {
            end--;
        }

        if (end < start)
        {
            return new Utf8Slice(offset, 0);
        }

        return new Utf8Slice(start, end - start + 1);
    }

    private static int[] BuildLineStarts(byte[] source)
    {
        var starts = new List<int>(64) { 0 };
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == (byte)'\n')
            {
                var next = i + 1;
                if (next < source.Length)
                {
                    starts.Add(next);
                }
            }
        }

        return starts.ToArray();
    }

    private static (int Line, int Column) OffsetToLineColumn(int[] lineStarts, int offset)
    {
        var idx = Array.BinarySearch(lineStarts, offset);
        if (idx >= 0)
        {
            return (idx + 1, 1);
        }

        idx = ~idx - 1;
        if (idx < 0)
        {
            return (1, offset + 1);
        }

        return (idx + 1, offset - lineStarts[idx] + 1);
    }

    private static int IndexOf(byte[] source, int start, ReadOnlySpan<byte> pattern)
    {
        if (pattern.IsEmpty || start >= source.Length)
        {
            return -1;
        }

        for (var i = start; i <= source.Length - pattern.Length; i++)
        {
            if (source.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsWhiteSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
