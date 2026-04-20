using System.Buffers.Text;
using System.Text;
using Seiton.Core.Parsing.Ast;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    internal static BoolNode? ParseBool<TReader>(ref TReader reader, List<Diagnostic> diagnostics, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseBool(ref reader, out var needsError, out var errorMark);
        if (needsError) AddError(diagnostics, errorMessage, errorMark);
        return node;
    }

    internal static BoolNode? ParseBool<TReader>(ref TReader reader, out bool needsError, out TextPosition errorMark)
        where TReader : IYamlStreamReader, allows ref struct
    {
        needsError = false;
        errorMark = default;

        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        if (!TryParseBool(valueUtf8, tag, out var value))
        {
            needsError = true;
            errorMark = mark;
            reader.Read();
            return null;
        }

        var node = new BoolNode
        {
            Value = value,
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };
        reader.Read();
        return node;
    }

    internal static StringNode? MayParseExpression<TReader>(
        ref TReader reader,
        List<Diagnostic> diagnostics,
        ExpressionValidationContext context)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.End || reader.CurrentKind != YamlEventKind.Scalar)
        {
            return null;
        }

        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        var mark = valueUtf8.Length > 0
            ? reader.ComputePositionFromOffset(slice.Offset)
            : reader.CurrentStart;
        var hasExpression = valueUtf8.IndexOf("${{"u8) >= 0;
        var node = new StringNode
        {
            Value = slice,
            Quoted = reader.IsScalarQuoted(),
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };

        if (hasExpression)
        {
            ValidateExpressionText(
                valueUtf8,
                BuildScalarLocation(mark, valueUtf8.Length),
                context,
                diagnostics,
                parseWholeValueIfNoEmbedded: false);
        }

        reader.Read();
        return hasExpression ? node : null;
    }

    internal static StringNode? ParseString<TReader>(ref TReader reader, List<Diagnostic> diagnostics, string errorMessage, bool allowEmpty = false)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseString(ref reader, out var needsError, out var errorMark, allowEmpty);
        if (needsError) AddError(diagnostics, errorMessage, errorMark);
        return node;
    }

    internal static StringNode? ParseString<TReader>(ref TReader reader, out bool needsError, out TextPosition errorMark, bool allowEmpty = false)
        where TReader : IYamlStreamReader, allows ref struct
    {
        needsError = false;
        errorMark = default;

        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return null;
        }

        // Use GetScalarSlice().Offset to derive position for non-empty scalars: the slice offset is
        // computed by searching the source bytes and is reliable, whereas reader.CurrentStart for some
        // YAML adapters (e.g. VYaml) may have already advanced past the scalar to the next token.
        // For empty scalars, CurrentStart uses a backward-scan heuristic that is more accurate than
        // the cursor-based Slice.Offset GetScalarSlice() returns for the empty case.
        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        var mark = valueUtf8.Length > 0
            ? reader.ComputePositionFromOffset(slice.Offset)
            : reader.CurrentStart;
        if (!allowEmpty && valueUtf8.Length == 0)
        {
            needsError = true;
            errorMark = mark;
        }

        var node = new StringNode
        {
            Value = slice,
            Quoted = reader.IsScalarQuoted(),
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };

        reader.Read();
        return node;
    }

    internal static StringNode? ParseExpression<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ExpressionValidationContext context, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseExpression(ref reader, diagnostics, context, out var needsError, out var errorMark);
        if (needsError) AddError(diagnostics, errorMessage, errorMark);
        return node;
    }

    internal static StringNode? ParseExpression<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ExpressionValidationContext context, out bool needsError, out TextPosition errorMark)
        where TReader : IYamlStreamReader, allows ref struct
    {
        needsError = false;
        errorMark = default;

        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return null;
        }

        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        var mark = valueUtf8.Length > 0
            ? reader.ComputePositionFromOffset(slice.Offset)
            : reader.CurrentStart;
        ValidateExpressionText(
            valueUtf8,
            BuildScalarLocation(mark, valueUtf8.Length),
            context,
            diagnostics,
            parseWholeValueIfNoEmbedded: true);

        var node = new StringNode
        {
            Value = slice,
            Quoted = reader.IsScalarQuoted(),
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };

        reader.Read();
        return node;
    }

    private static StringNode? ParseStringAndValidateExpression<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ExpressionValidationContext context, string errorMessage, bool parseWholeValueIfNoEmbedded)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseStringAndValidateExpression(ref reader, diagnostics, context, out var needsError, out var errorMark, parseWholeValueIfNoEmbedded);
        if (needsError) AddError(diagnostics, errorMessage, errorMark);
        return node;
    }

    private static StringNode? ParseStringAndValidateExpression<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ExpressionValidationContext context, out bool needsError, out TextPosition errorMark, bool parseWholeValueIfNoEmbedded)
        where TReader : IYamlStreamReader, allows ref struct
    {
        needsError = false;
        errorMark = default;

        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return null;
        }

        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        var mark = valueUtf8.Length > 0
            ? reader.ComputePositionFromOffset(slice.Offset)
            : reader.CurrentStart;
        var range = BuildScalarLocation(mark, valueUtf8.Length);
        ValidateExpressionText(
            valueUtf8,
            range,
            context,
            diagnostics,
            parseWholeValueIfNoEmbedded);

        var node = new StringNode
        {
            Value = slice,
            Quoted = reader.IsScalarQuoted(),
            Range = range,
        };

        reader.Read();
        return node;
    }

    internal static StringNode[] ParseStringOrStringSequence<TReader>(ref TReader reader, List<Diagnostic> diagnostics, string errorMessage, bool allowEmpty = false, bool allowElemEmpty = false)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var nodes = ParseStringOrStringSequence(ref reader, diagnostics, out var needsError, out var errorMark, allowEmpty, allowElemEmpty);
        if (needsError) AddError(diagnostics, errorMessage, errorMark);
        return nodes;
    }

    internal static StringNode[] ParseStringOrStringSequence<TReader>(ref TReader reader, List<Diagnostic> diagnostics, out bool needsError, out TextPosition errorMark, bool allowEmpty = false, bool allowElemEmpty = false)
        where TReader : IYamlStreamReader, allows ref struct
    {
        needsError = false;
        errorMark = default;

        if (reader.End)
        {
            return [];
        }

        if (reader.CurrentKind == YamlEventKind.Scalar)
        {
            var single = ParseString(ref reader, out needsError, out errorMark, allowEmpty);
            return single is null ? [] : [single];
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return [];
        }

        var list = new List<StringNode>(4);
        reader.Read();
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            var node = ParseString(ref reader, out needsError, out errorMark, allowElemEmpty);
            if (needsError)
            {
                // Element-level error: use the same errorMessage pattern
                // The caller will provide the error message, so just propagate the first error
                break;
            }
            if (node is not null)
            {
                list.Add(node);
            }
        }

        // Continue reading remaining elements even after error
        while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
        {
            reader.SkipCurrentNode();
        }

        if (reader.CurrentKind == YamlEventKind.SequenceEnd)
        {
            reader.Read();
        }

        return list.ToArray();
    }

    internal static FloatNode? ParseFloat<TReader>(ref TReader reader, List<Diagnostic> diagnostics, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseFloat(ref reader, out var needsError, out var errorMark);
        if (needsError) AddError(diagnostics, errorMessage, errorMark);
        return node;
    }

    internal static FloatNode? ParseFloat<TReader>(ref TReader reader, out bool needsError, out TextPosition errorMark)
        where TReader : IYamlStreamReader, allows ref struct
    {
        needsError = false;
        errorMark = default;

        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        if (!TryParseDouble(valueUtf8, tag, out var value))
        {
            needsError = true;
            errorMark = mark;
            reader.Read();
            return null;
        }

        var node = new FloatNode
        {
            Value = value,
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };
        reader.Read();
        return node;
    }

    internal static IntNode? ParseInt<TReader>(ref TReader reader, List<Diagnostic> diagnostics, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseInt(ref reader, out var needsError, out var errorMark);
        if (needsError) AddError(diagnostics, errorMessage, errorMark);
        return node;
    }

    internal static IntNode? ParseInt<TReader>(ref TReader reader, out bool needsError, out TextPosition errorMark)
        where TReader : IYamlStreamReader, allows ref struct
    {
        needsError = false;
        errorMark = default;

        if (reader.End)
        {
            return null;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return null;
        }

        var mark = reader.CurrentStart;
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        if (!TryParseInt64(valueUtf8, tag, out var value))
        {
            needsError = true;
            errorMark = mark;
            reader.Read();
            return null;
        }

        var node = new IntNode
        {
            Value = value,
            Range = BuildScalarLocation(mark, valueUtf8.Length),
        };
        reader.Read();
        return node;
    }

    private static bool TryParseBool(ReadOnlySpan<byte> valueUtf8, ScalarTag tag, out bool value)
    {
        if (tag == ScalarTag.Bool)
        {
            if (valueUtf8.SequenceEqual("true"u8))
            {
                value = true;
                return true;
            }

            if (valueUtf8.SequenceEqual("false"u8))
            {
                value = false;
                return true;
            }
        }

        value = false;
        return false;
    }

    private static bool TryParseInt64(ReadOnlySpan<byte> valueUtf8, ScalarTag tag, out long value)
    {
        if (tag is ScalarTag.Int or ScalarTag.Unknown)
        {
            if (Utf8Parser.TryParse(valueUtf8, out value, out var consumed) && consumed == valueUtf8.Length)
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryParseDouble(ReadOnlySpan<byte> valueUtf8, ScalarTag tag, out double value)
    {
        if (tag is ScalarTag.Float or ScalarTag.Int or ScalarTag.Unknown)
        {
            if (Utf8Parser.TryParse(valueUtf8, out value, out var consumed) && consumed == valueUtf8.Length)
            {
                return true;
            }
        }

        value = default;
        return false;
    }


    private static void ParseConditionalExpression<TReader>(ref TReader reader, List<Diagnostic> diagnostics, ExpressionValidationContext context, string shapeError)
        where TReader : IYamlStreamReader, allows ref struct
    {
        _ = ParseExpression(ref reader, diagnostics, context, shapeError);
    }

    private static void ValidateExpressionText(ReadOnlySpan<byte> valueUtf8, TextRange valueLocation, ExpressionValidationContext context, List<Diagnostic> diagnostics, bool parseWholeValueIfNoEmbedded)
    {
        var hasEmbedded = false;
        var i = 0;
        while (i + 3 < valueUtf8.Length)
        {
            if (valueUtf8[i] == (byte)'$' && valueUtf8[i + 1] == (byte)'{' && valueUtf8[i + 2] == (byte)'{')
            {
                hasEmbedded = true;
                var exprStart = i + 3;
                var end = IndexOf(valueUtf8, exprStart, "}}"u8);
                if (end < 0)
                {
                    break;
                }

                var trimmed = TrimAsciiWhiteSpace(valueUtf8, exprStart, end - exprStart);
                if (trimmed.Length > 0)
                {
                    var expressionUtf8 = valueUtf8.Slice(trimmed.Offset, trimmed.Length);
                    var expressionLocation = ShiftLocation(valueLocation, trimmed.Offset, trimmed.Length);
                    ParseAndValidateExpression(expressionUtf8, expressionLocation, context, diagnostics);
                }

                i = end + 2;
                continue;
            }

            i++;
        }

        if (!hasEmbedded && parseWholeValueIfNoEmbedded)
        {
            var trimmed = TrimAsciiWhiteSpace(valueUtf8, 0, valueUtf8.Length);
            if (trimmed.Length <= 0)
            {
                return;
            }

            ParseAndValidateExpression(valueUtf8.Slice(trimmed.Offset, trimmed.Length), ShiftLocation(valueLocation, trimmed.Offset, trimmed.Length), context, diagnostics);
        }
    }

    private static void ParseAndValidateExpression(ReadOnlySpan<byte> expressionUtf8, TextRange expressionLocation, ExpressionValidationContext context, List<Diagnostic> diagnostics)
    {
        var parseResult = ExpressionParser.Parse(expressionUtf8);
        for (var i = 0; i < parseResult.Diagnostics.Length; i++)
        {
            var parseDiagnostic = parseResult.Diagnostics[i];
            diagnostics.Add(new Diagnostic(parseDiagnostic.Severity, $"expression parse error: {parseDiagnostic.Message}", ShiftLocation(expressionLocation, parseDiagnostic.Location.Start, parseDiagnostic.Location.Length)));
        }

        var semanticDiagnostics = ExpressionSemanticAnalyzer.Validate(parseResult, expressionUtf8, expressionLocation, context);
        for (var i = 0; i < semanticDiagnostics.Length; i++)
        {
            diagnostics.Add(semanticDiagnostics[i]);
        }
    }

    private static bool ContainsExpression(ReadOnlySpan<byte> valueUtf8)
    {
        for (var i = 0; i + 2 < valueUtf8.Length; i++)
        {
            if (valueUtf8[i] == (byte)'$'
                && valueUtf8[i + 1] == (byte)'{'
                && valueUtf8[i + 2] == (byte)'{')
            {
                return true;
            }
        }

        return false;
    }

    private static TextRange BuildScalarLocation(TextPosition mark, int length)
    {
        var safeLength = length <= 0 ? 1 : length;
        return new TextRange(
            Start: mark.Position,
            Length: safeLength,
            StartLine: mark.Line,
            StartColumn: mark.Col,
            EndLine: mark.Line,
            EndColumn: mark.Col + safeLength - 1);
    }

    private static TextRange BuildCompositeLocation(TextPosition start, TextPosition end)
    {
        var safeLength = end.Position >= start.Position
            ? (end.Position - start.Position) + 1
            : 1;
        var endLine = end.Line <= 0 ? start.Line : end.Line;
        var endColumn = end.Col <= 0 ? start.Col : end.Col;
        return new TextRange(
            Start: start.Position,
            Length: safeLength,
            StartLine: start.Line,
            StartColumn: start.Col,
            EndLine: endLine,
            EndColumn: endColumn);
    }

    private static TextRange BuildCompositeLocation(TextPosition start, TextRange end)
    {
        return BuildCompositeLocation(start, new TextPosition(end.Start + end.Length - 1, end.EndLine, end.EndColumn));
    }

    private static TextRange ShiftLocation(TextRange baseLocation, int relativeOffset, int length)
    {
        var safeLength = length <= 0 ? 1 : length;
        return new TextRange(
            Start: baseLocation.Start + relativeOffset,
            Length: safeLength,
            StartLine: baseLocation.StartLine,
            StartColumn: baseLocation.StartColumn + relativeOffset,
            EndLine: baseLocation.EndLine,
            EndColumn: baseLocation.StartColumn + relativeOffset + safeLength - 1);
    }

    private static TextRange BuildLocationFromSourceSlice(ReadOnlySpan<byte> source, int startOffset, int length)
    {
        var safeLength = length <= 0 ? 1 : length;
        if ((uint)startOffset >= (uint)source.Length)
        {
            return new TextRange(startOffset, safeLength, 1, 1, 1, safeLength);
        }

        var endOffset = startOffset + safeLength - 1;
        if (endOffset >= source.Length)
        {
            endOffset = source.Length - 1;
        }

        var start = ComputeLineColumn(source, startOffset);
        var end = ComputeLineColumn(source, endOffset);
        return new TextRange(
            Start: startOffset,
            Length: safeLength,
            StartLine: start.Line,
            StartColumn: start.Column,
            EndLine: end.Line,
            EndColumn: end.Column);
    }
    private static bool TryRegisterMappingKey(ReadOnlySpan<byte> keyUtf8, TextPosition keyMark, List<Diagnostic> diagnostics, HashSet<Utf8String> keys, MappingKeyComparison comparison, string mappingName)
    {
        if (keyUtf8.SequenceEqual("<<"u8))
        {
            AddError(diagnostics, $"{mappingName} does not support merge key '<<'", keyMark);
            return false;
        }

        var normalizedKey = comparison == MappingKeyComparison.CaseSensitive
            ? new Utf8String(keyUtf8)
            : Utf8String.FromLowerAscii(keyUtf8);
        if (keys.Add(normalizedKey))
        {
            return true;
        }

        AddError(diagnostics, $"{mappingName} contains duplicate key: {Encoding.UTF8.GetString(keyUtf8)}", keyMark);
        return false;
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> source, Utf8Slice slice)
    {
        return Encoding.UTF8.GetString(slice.AsSpan(source));
    }

    private static string FormatContainerSectionName(ReadOnlySpan<byte> source, Utf8Slice jobId, Utf8Slice serviceName, bool isService)
    {
        var jobIdText = DecodeUtf8(source, jobId);
        if (!isService)
        {
            return $"job '{jobIdText}' container";
        }

        return $"job '{jobIdText}' service '{DecodeUtf8(source, serviceName)}'";
    }

    private static void AddError(List<Diagnostic> diagnostics, string message, TextPosition mark)
    {
        var location = new TextRange(
            Start: mark.Position,
            Length: 0,
            StartLine: mark.Line,
            StartColumn: mark.Col,
            EndLine: mark.Line,
            EndColumn: mark.Col);

        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, location));
    }

}
