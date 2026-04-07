using VYaml.Parser;

namespace Seiton.Core.Parsing;

public readonly record struct ExpressionOccurrence(
    byte[] Utf8,
    TextRange Location);

public static class ExpressionExtractor
{
    public static ExpressionOccurrence[] Extract(byte[] utf8Yaml)
    {
        var reader = new VYamlStreamReader(utf8Yaml.AsMemory());
        var expressions = new List<ExpressionOccurrence>();

        reader.SkipHeader();
        while (!reader.End)
        {
            if (reader.CurrentEventType == ParseEventType.Scalar)
            {
                var scalarUtf8 = reader.GetScalarUtf8();
                if (!scalarUtf8.IsEmpty)
                {
                    ExtractFromScalar(scalarUtf8, reader.CurrentMark, expressions);
                }
            }

            reader.Read();
        }

        return expressions.ToArray();
    }

    public static (ExpressionOccurrence[] Occurrences, Diagnostic[] Diagnostics) ExtractAndParse(byte[] utf8Yaml)
    {
        var occurrences = Extract(utf8Yaml);
        var diagnostics = new List<Diagnostic>();

        foreach (var occurrence in occurrences)
        {
            var expression = occurrence.Utf8.AsSpan();
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

    private static void ExtractFromScalar(ReadOnlySpan<byte> scalarUtf8, Marker mark, List<ExpressionOccurrence> expressions)
    {
        var searchStart = 0;
        while (searchStart < scalarUtf8.Length)
        {
            var start = scalarUtf8[searchStart..].IndexOf("${{"u8);
            if (start < 0)
            {
                break;
            }

            start += searchStart;

            var bodyStart = start + 3;
            var end = scalarUtf8[bodyStart..].IndexOf("}}"u8);
            if (end < 0)
            {
                break;
            }

            end += bodyStart;

            var bodyTrimmed = TrimAsciiWhiteSpace(scalarUtf8, bodyStart, end - bodyStart);
            if (bodyTrimmed.Length > 0)
            {
                var location = new TextRange(
                    Start: mark.Position,
                    Length: bodyTrimmed.Length,
                    StartLine: mark.Line,
                    StartColumn: mark.Col,
                    EndLine: mark.Line,
                    EndColumn: mark.Col + bodyTrimmed.Length - 1);
                var utf8 = scalarUtf8.Slice(bodyTrimmed.Offset, bodyTrimmed.Length).ToArray();
                expressions.Add(new ExpressionOccurrence(
                    utf8,
                    location));
            }

            searchStart = end + 2;
        }
    }

    private static Utf8Slice TrimAsciiWhiteSpace(ReadOnlySpan<byte> source, int offset, int length)
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

    private static bool IsWhiteSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
