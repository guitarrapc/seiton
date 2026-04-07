using VYaml.Parser;

namespace Seiton.Core.Parsing;

public readonly record struct ExpressionOccurrence(
    string Expression,
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
                var scalar = reader.GetScalarString();
                if (!string.IsNullOrEmpty(scalar))
                {
                    ExtractFromScalar(scalar, reader.CurrentMark, expressions);
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
            var result = ExpressionParser.Parse(occurrence.Expression);
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

    private static void ExtractFromScalar(string scalar, Marker mark, List<ExpressionOccurrence> expressions)
    {
        var searchStart = 0;
        while (searchStart < scalar.Length)
        {
            var start = scalar.IndexOf("${{", searchStart, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var bodyStart = start + 3;
            var end = scalar.IndexOf("}}", bodyStart, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            var body = scalar.Substring(bodyStart, end - bodyStart).Trim();
            if (body.Length > 0)
            {
                var location = new TextRange(
                    Start: mark.Position + start,
                    Length: end + 2 - start,
                    StartLine: mark.Line,
                    StartColumn: mark.Col + start,
                    EndLine: mark.Line,
                    EndColumn: mark.Col + end + 1);
                expressions.Add(new ExpressionOccurrence(body, location));
            }

            searchStart = end + 2;
        }
    }
}
