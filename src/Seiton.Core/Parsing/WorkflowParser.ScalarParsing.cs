using System.Buffers.Text;
using System.Text;

using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    internal static BoolNodeId ParseBool<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseBool(ref reader, arena, out var needsError, out var errorMark);
        if (needsError) AddError(ref diagnostics, errorMessage, errorMark);
        return node;
    }

    internal static BoolNodeId ParseBool<TReader>(ref TReader reader, AstArena arena, out bool needsError, out TextPosition errorMark)
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
        if (!TryParseBool(valueUtf8, tag, out var value))
        {
            needsError = true;
            errorMark = mark;
            reader.Read();
            return default;
        }

        var node = arena.AddBool(value, BuildScalarLocation(mark, valueUtf8.Length));
        reader.Read();
        return node;
    }

    internal static StringNodeId ParseString<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, string errorMessage, bool allowEmpty = false)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseString(ref reader, arena, out var needsError, out var errorMark, allowEmpty);
        if (needsError) AddError(ref diagnostics, errorMessage, errorMark);
        return node;
    }

    internal static StringNodeId ParseString<TReader>(ref TReader reader, AstArena arena, out bool needsError, out TextPosition errorMark, bool allowEmpty = false)
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

        // Use GetScalarSlice().Offset to derive position for non-empty scalars: the slice offset is
        // computed by searching the source bytes and is reliable, whereas reader.CurrentStart for some
        // YAML adapters (e.g. VYaml) may have already advanced past the scalar to the next token.
        // For empty scalars, CurrentStart uses a backward-scan heuristic that is more accurate than
        // the cursor-based Slice.Offset GetScalarSlice() returns for the empty case.
        var isQuoted = reader.IsScalarQuoted();
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

        var node = arena.AddString(slice, isQuoted, BuildScalarLocation(mark, valueUtf8.Length));

        reader.Read();
        return node;
    }

    internal static StringNodeId[] ParseStringOrStringSequence<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, string errorMessage, bool allowEmpty = false, bool allowElemEmpty = false)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var nodes = ParseStringOrStringSequence(ref reader, arena, ref diagnostics, out var needsError, out var errorMark, allowEmpty, allowElemEmpty);
        if (needsError) AddError(ref diagnostics, errorMessage, errorMark);
        return nodes;
    }

    internal static StringNodeId[] ParseStringOrStringSequence<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, out bool needsError, out TextPosition errorMark, bool allowEmpty = false, bool allowElemEmpty = false, string? emptyElementMessage = null)
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
            var single = ParseString(ref reader, arena, out needsError, out errorMark, allowEmpty);
            return !single.HasValue ? [] : [single];
        }

        if (reader.CurrentKind != YamlEventKind.SequenceStart)
        {
            needsError = true;
            errorMark = reader.CurrentStart;
            reader.SkipCurrentNode();
            return [];
        }

        var list = new PooledBuffer<StringNodeId>(4);
        try
        {
            reader.Read();
            while (!reader.End && reader.CurrentKind != YamlEventKind.SequenceEnd)
            {
                var node = ParseString(ref reader, arena, out var elemError, out var elemMark, allowElemEmpty);
                if (elemError)
                {
                    // Record first element error for caller, but continue parsing remaining
                    // elements so that downstream lint rules (e.g. GlobPatternRule) can validate them.
                    if (!needsError)
                    {
                        needsError = true;
                        errorMark = elemMark;
                    }
                    continue;
                }
                if (node.HasValue)
                {
                    // Report empty elements as errors only when caller provides a message.
                    if (emptyElementMessage is not null && allowElemEmpty && arena.GetStringValue(node).Length == 0)
                    {
                        var range = arena.GetStringRange(node);
                        AddError(ref diagnostics, emptyElementMessage, new TextPosition(range.Start, range.StartLine, range.StartColumn));
                    }
                    list.Add(node);
                }
            }

            if (reader.CurrentKind == YamlEventKind.SequenceEnd)
            {
                reader.Read();
            }

            return list.ToArray();
        }
        finally { list.Dispose(); }
    }

    internal static FloatNodeId ParseFloat<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseFloat(ref reader, arena, out var needsError, out var errorMark);
        if (needsError) AddError(ref diagnostics, errorMessage, errorMark);
        return node;
    }

    internal static FloatNodeId ParseFloat<TReader>(ref TReader reader, AstArena arena, out bool needsError, out TextPosition errorMark)
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
        if (!TryParseDouble(valueUtf8, tag, out var value))
        {
            needsError = true;
            errorMark = mark;
            reader.Read();
            return default;
        }

        var node = arena.AddFloat(value, BuildScalarLocation(mark, valueUtf8.Length));
        reader.Read();
        return node;
    }

    internal static IntNodeId ParseInt<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        var node = ParseInt(ref reader, arena, out var needsError, out var errorMark);
        if (needsError) AddError(ref diagnostics, errorMessage, errorMark);
        return node;
    }

    internal static IntNodeId ParseInt<TReader>(ref TReader reader, AstArena arena, out bool needsError, out TextPosition errorMark)
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
        if (!TryParseInt64(valueUtf8, tag, out var value))
        {
            needsError = true;
            errorMark = mark;
            reader.Read();
            return default;
        }

        var node = arena.AddInt(value, BuildScalarLocation(mark, valueUtf8.Length));
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

    /// <summary>
    /// Sets the specified bit in the seen mask.
    /// Returns true if the bit was not already set (key is new); false if duplicate.
    /// </summary>
    private static bool TrySetBit(ref ulong seen, int bit)
    {
        var mask = 1UL << bit;
        if ((seen & mask) != 0) return false;
        seen |= mask;
        return true;
    }

    /// <summary>
    /// Checks if the key is the YAML merge key '&lt;&lt;' and rejects it.
    /// Returns true if the key IS a merge key (caller should skip key+value).
    /// VYaml's CurrentMark for the '&lt;&lt;' key points past the key text (at the ':'),
    /// so we adjust the position back by the key length to report the correct column.
    /// </summary>
    private static bool IsMergeKey(ReadOnlySpan<byte> keyUtf8, TextPosition keyMark, ref PooledBuffer<Diagnostic> diagnostics, string mappingName)
    {
        if (!keyUtf8.SequenceEqual("<<"u8)) return false;
        AddError(ref diagnostics, $"GitHub Actions does not support YAML merge key \"<<\". occurred in {mappingName}", keyMark);
        return true;
    }

    /// <summary>
    /// Registers a dynamic (user-defined) mapping key for duplicate detection.
    /// Uses offset-based storage in a stackalloc buffer to avoid heap allocation.
    /// Returns true if the key is new; false if duplicate or merge key.
    /// </summary>
    private static bool TryRegisterDynamicKey(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> keyUtf8,
        int keyOffset,
        int keyLength,
        TextPosition keyMark,
        ref PooledBuffer<Diagnostic> diagnostics,
        Span<long> keyStore,
        ref int keyCount,
        bool caseSensitive,
        string mappingName)
    {
        if (keyUtf8.SequenceEqual("<<"u8))
        {
            AddError(ref diagnostics, $"GitHub Actions does not support YAML merge key \"<<\". occurred in {mappingName}", keyMark);
            return false;
        }

        for (var i = 0; i < keyCount; i++)
        {
            var prevOffset = (int)(keyStore[i] >> 32);
            var prevLength = (int)(keyStore[i] & 0xFFFFFFFF);
            var prev = source.Slice(prevOffset, prevLength);
            var isMatch = caseSensitive
                ? prev.SequenceEqual(keyUtf8)
                : EqualsAsciiIgnoreCase(prev, keyUtf8);
            if (isMatch)
            {
                var keyText = Encoding.UTF8.GetString(keyUtf8);
                var sectionName = ExtractSectionDisplayName(mappingName);
                var (prevLine, prevCol) = ComputeLineColumn(source, prevOffset);
                var caseNote = caseSensitive ? "" : ". note that this key is case insensitive";
                AddError(ref diagnostics, $"key \"{keyText}\" is duplicated in \"{sectionName}\" section. previously defined at line:{prevLine},col:{prevCol}{caseNote}", keyMark);
                return false;
            }
        }

        if (keyCount < keyStore.Length)
        {
            keyStore[keyCount] = ((long)keyOffset << 32) | (uint)keyLength;
            keyCount++;
        }

        return true;
    }

    /// <summary>Extracts the last segment of a dotted or spaced mapping name for display (e.g. "on.workflow_call.inputs" → "inputs").</summary>
    private static string ExtractSectionDisplayName(string mappingName)
    {
        var dotIndex = mappingName.LastIndexOf('.');
        if (dotIndex >= 0) return mappingName.Substring(dotIndex + 1);
        var spaceIndex = mappingName.LastIndexOf(' ');
        if (spaceIndex >= 0) return mappingName.Substring(spaceIndex + 1);
        return mappingName;
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
            return $"jobs.'{jobIdText}'.container";
        }

        return $"jobs.'{jobIdText}'.services.'{DecodeUtf8(source, serviceName)}'";
    }

    private static void AddError(ref PooledBuffer<Diagnostic> diagnostics, string message, TextPosition mark)
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

    private static void AddFatalParseError(ref PooledBuffer<Diagnostic> diagnostics, string message, TextPosition mark, string? help)
    {
        var location = new TextRange(
            Start: mark.Position,
            Length: 0,
            StartLine: mark.Line,
            StartColumn: mark.Col,
            EndLine: mark.Line,
            EndColumn: mark.Col);

        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, location, Help: help));
    }

    private static void AddError(ref PooledBuffer<Diagnostic> diagnostics, string message, TextPosition mark, DiagnosticFix? fix)
    {
        var location = new TextRange(
            Start: mark.Position,
            Length: 0,
            StartLine: mark.Line,
            StartColumn: mark.Col,
            EndLine: mark.Line,
            EndColumn: mark.Col);

        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, location, Fix: fix));
    }

    private static void AddWarning(ref PooledBuffer<Diagnostic> diagnostics, string message, TextPosition mark)
    {
        var location = new TextRange(
            Start: mark.Position,
            Length: 0,
            StartLine: mark.Line,
            StartColumn: mark.Col,
            EndLine: mark.Line,
            EndColumn: mark.Col);

        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, message, location));
    }

    /// <summary>Parses a YAML bool scalar into <see cref="BoolNodeId"/> (used by <c>on.*</c> metadata and action metadata).</summary>
    private static BoolNodeId ParseBoolNode<TReader>(ref TReader reader, AstArena arena, ref PooledBuffer<Diagnostic> diagnostics, string errorMessage)
        where TReader : IYamlStreamReader, allows ref struct
    {
        if (reader.End)
        {
            return default;
        }

        if (reader.CurrentKind != YamlEventKind.Scalar)
        {
            AddError(ref diagnostics, errorMessage, reader.CurrentStart);
            reader.SkipCurrentNode();
            return default;
        }

        var slice = reader.GetScalarSlice();
        var valueUtf8 = reader.GetScalarUtf8();
        var tag = reader.GetScalarTag();
        var mark = valueUtf8.Length > 0
            ? reader.ComputePositionFromOffset(slice.Offset)
            : reader.CurrentStart;
        if (!TryParseBool(valueUtf8, tag, out var value))
        {
            AddError(ref diagnostics, errorMessage, mark);
            reader.Read();
            return default;
        }

        var node = arena.AddBool(value, BuildScalarLocation(mark, valueUtf8.Length));
        reader.Read();
        return node;
    }
}
