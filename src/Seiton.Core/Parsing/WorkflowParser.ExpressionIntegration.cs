using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    internal static StringNodeId MayParseExpression<TReader>(
        ref TReader reader, AstArena arena,
        List<Diagnostic> diagnostics,
        ExpressionValidationContext context)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.End || reader.CurrentKind != YamlEventKind.Scalar)
        {
            return default;
        }

        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        var mark = valueUtf8.Length > 0
            ? reader.ComputePositionFromOffset(slice.Offset)
            : reader.CurrentStart;
        var hasExpression = valueUtf8.IndexOf("${{"u8) >= 0;
        var node = arena.AddString(slice, reader.IsScalarQuoted(), BuildScalarLocation(mark, valueUtf8.Length));

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
        return hasExpression ? node : default;
    }

    internal static StringNodeId ParseExpression<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ExpressionValidationContext context, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseExpression(ref reader, arena, diagnostics, context, out var needsError, out var errorMark);
        if (needsError) AddError(diagnostics, errorMessage, errorMark);
        return node;
    }

    internal static StringNodeId ParseExpression<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ExpressionValidationContext context, out bool needsError, out TextPosition errorMark)
        where TReader : IYamlStreamReader, allows ref struct
    {
        needsError = false;
        errorMark = default;

        if (reader.End)
        {
            return default;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return default;
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
            parseWholeValueIfNoEmbedded: true,
            allowStatusCheckFunctions: true);

        var node = arena.AddString(slice, reader.IsScalarQuoted(), BuildScalarLocation(mark, valueUtf8.Length));

        reader.Read();
        return node;
    }

    private static StringNodeId ParseStringAndValidateExpression<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ExpressionValidationContext context, string errorMessage, bool parseWholeValueIfNoEmbedded)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseStringAndValidateExpression(ref reader, arena, diagnostics, context, out var needsError, out var errorMark, parseWholeValueIfNoEmbedded);
        if (needsError) AddError(diagnostics, errorMessage, errorMark);
        return node;
    }

    private static StringNodeId ParseStringAndValidateExpression<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ExpressionValidationContext context, out bool needsError, out TextPosition errorMark, bool parseWholeValueIfNoEmbedded)
        where TReader : IYamlStreamReader, allows ref struct
    {
        needsError = false;
        errorMark = default;

        if (reader.End)
        {
            return default;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return default;
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

        var node = arena.AddString(slice, reader.IsScalarQuoted(), range);

        reader.Read();
        return node;
    }

    private static FloatNodeId ParseFloatOrExpression<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ExpressionValidationContext context, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseFloatOrExpression(ref reader, arena, diagnostics, context, out var needsError, out var errorMark);
        if (needsError) AddError(diagnostics, errorMessage, errorMark);
        return node;
    }

    private static FloatNodeId ParseFloatOrExpression<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ExpressionValidationContext context, out bool needsError, out TextPosition errorMark)
        where TReader : IYamlStreamReader, allows ref struct
    {
        needsError = false;
        errorMark = default;

        if (reader.End)
        {
            return default;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return default;
        }

        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        var mark = valueUtf8.Length > 0
            ? reader.ComputePositionFromOffset(slice.Offset)
            : reader.CurrentStart;
        var range = BuildScalarLocation(mark, valueUtf8.Length);

        if (TryParseDouble(valueUtf8, tag, out var value))
        {
            var floatNode = arena.AddFloat(value, range);
            reader.Read();
            return floatNode;
        }

        var expressionNode = ParseStringAndValidateExpression(ref reader, arena, diagnostics, context, out needsError, out errorMark, parseWholeValueIfNoEmbedded: false);
        if (!expressionNode.HasValue)
        {
            return default;
        }

        // If the string doesn't contain an expression, it's not a valid number
        if (!arena.GetStringExpression(expressionNode).HasValue && arena.GetStringValue(expressionNode).IndexOf("${{"u8) < 0)
        {
            needsError = true;
            errorMark = mark;
            return default;
        }

        return arena.AddFloat(0, expressionNode, range);
    }

    private static void ParseConditionalExpression<TReader>(ref TReader reader, AstArena arena, List<Diagnostic> diagnostics, ExpressionValidationContext context, string shapeError)
        where TReader : IYamlStreamReader, allows ref struct
    {
        _ = ParseExpression(ref reader, arena, diagnostics, context, shapeError);
    }

    private static void ValidateExpressionText(ReadOnlySpan<byte> valueUtf8, TextRange valueLocation, ExpressionValidationContext context, List<Diagnostic> diagnostics, bool parseWholeValueIfNoEmbedded, bool allowStatusCheckFunctions = false)
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
                    ParseAndValidateExpression(expressionUtf8, expressionLocation, context, diagnostics, allowStatusCheckFunctions);
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

            ParseAndValidateExpression(valueUtf8.Slice(trimmed.Offset, trimmed.Length), ShiftLocation(valueLocation, trimmed.Offset, trimmed.Length), context, diagnostics, allowStatusCheckFunctions);
        }
    }

    private static void ParseAndValidateExpression(ReadOnlySpan<byte> expressionUtf8, TextRange expressionLocation, ExpressionValidationContext context, List<Diagnostic> diagnostics, bool allowStatusCheckFunctions = false)
    {
        ExpressionParser.ParseAndValidateInline(expressionUtf8, expressionLocation, context, diagnostics, allowStatusCheckFunctions);
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
}
