using static Seiton.Core.Parsing.SpanHelpers;
using static Seiton.Core.Parsing.ExpressionScanHelpers;

namespace Seiton.Core.Parsing;

/// <summary>A located <c>${{ ... }}</c> expression occurrence within the YAML source.</summary>
public readonly record struct ExpressionOccurrence(
    Utf8Slice Slice,
    TextRange Location);

public static class ExpressionExtractor
{
    /// <summary>Extracts all <c>${{ ... }}</c> expression occurrences from the UTF-8 YAML bytes.</summary>
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

    /// <summary>Extracts all expressions and parses each one, returning occurrences and parse diagnostics.</summary>
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

    /// <summary>Extracts, parses, and semantically validates all expressions in the given context.</summary>
    public static (ExpressionOccurrence[] Occurrences, Diagnostic[] Diagnostics) ExtractParseAndValidate(
        byte[] utf8Yaml,
        ExpressionValidationContext context)
    {
        var occurrences = Extract(utf8Yaml);
        var diagnostics = new List<Diagnostic>();

        foreach (var occurrence in occurrences)
        {
            var expression = occurrence.Slice.AsSpan(utf8Yaml);
            var parseResult = ExpressionParser.Parse(expression);
            for (var i = 0; i < parseResult.Diagnostics.Length; i++)
            {
                diagnostics.Add(new Diagnostic(
                    parseResult.Diagnostics[i].Severity,
                    $"expression parse error: {parseResult.Diagnostics[i].Message}",
                    occurrence.Location));
            }

            var semanticDiagnostics = ExpressionSemanticAnalyzer.Validate(
                parseResult,
                expression,
                occurrence.Location,
                context);
            for (var i = 0; i < semanticDiagnostics.Length; i++)
            {
                diagnostics.Add(semanticDiagnostics[i]);
            }
        }

        return (occurrences, diagnostics.ToArray());
    }
}
