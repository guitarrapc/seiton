using System.Buffers.Text;
using VYaml.Parser;

namespace Seiton.Core.Parsing;

internal ref struct VYamlStreamAdapter : IYamlStreamReader
{
    private YamlParser _parser;
    private readonly Memory<byte> _source;
    private int _scalarSliceCursor;

    public VYamlStreamAdapter(Memory<byte> bytes)
    {
        _source = bytes;
        _scalarSliceCursor = 0;
        _parser = YamlParser.FromBytes(bytes);
    }

    public YamlEventKind CurrentKind => MapEventKind(_parser.CurrentEventType);

    public bool End => _parser.End;

    public TextPosition CurrentStart
    {
        get
        {
            var mark = _parser.CurrentMark;
            // VYaml's CurrentMark for empty/null scalars advances past the token to the next token's
            // position rather than staying at the scalar itself. Use the backward-scan helper to recover
            // the actual position. Check IsNullScalar() first because GetScalarAsUtf8() throws when the
            // VYaml internal currentScalar is null (implicit-null scalars like "key:" with no value).
            if (_parser.CurrentEventType == ParseEventType.Scalar
                && (_parser.IsNullScalar() || _parser.GetScalarAsUtf8().Length == 0))
            {
                var correctedOffset = ResolveEmptyScalarStart(mark.Position);
                return ComputeTextPositionFromOffset(_source.Span, correctedOffset);
            }

            return new TextPosition(mark.Position, mark.Line, mark.Col);
        }
    }

    public TextPosition CurrentEnd => CurrentStart;

    public bool Read() => _parser.Read();

    public void SkipHeader() => _parser.SkipAfter(ParseEventType.DocumentStart);

    public void SkipCurrentNode() => _parser.SkipCurrentNode();

    public void SkipAfter(YamlEventKind kind) => _parser.SkipAfter(MapEventKind(kind));

    public ReadOnlySpan<byte> GetScalarUtf8() => _parser.GetScalarAsUtf8();

    public Utf8Slice GetScalarSlice()
    {
        var utf8 = _parser.GetScalarAsUtf8();
        if (utf8.IndexOf((byte)'\n') >= 0
            && TryResolveNormalizedSlice(utf8, out var normalizedStart, out var normalizedLength))
        {
            _scalarSliceCursor = normalizedStart + normalizedLength;
            return new Utf8Slice(normalizedStart, normalizedLength);
        }

        if (_parser.TryGetScalarAsSpan(out var raw) && TryResolveRawStart(raw, out var rawStart))
        {
            _scalarSliceCursor = rawStart + raw.Length;
            return new Utf8Slice(rawStart, raw.Length);
        }

        if (utf8.Length == 0)
        {
            // For empty scalars, return the current cursor position without advancing it.
            // CurrentStart handles the backward-scan for accurate position reporting of empty scalars.
            var emptyStart = _scalarSliceCursor <= _source.Length ? _scalarSliceCursor : _source.Length;
            return new Utf8Slice(emptyStart, 0);
        }

        var source = _source.Span;
        var start = -1;
        if (_scalarSliceCursor <= source.Length - utf8.Length)
        {
            var idx = source[_scalarSliceCursor..].IndexOf(utf8);
            if (idx >= 0)
            {
                start = _scalarSliceCursor + idx;
            }
        }

        if (start < 0)
        {
            var mark = _parser.CurrentMark;
            var maxStart = source.Length - utf8.Length;
            if (maxStart < 0)
            {
                maxStart = 0;
            }

            start = mark.Position;
            if (start < 0)
            {
                start = 0;
            }
            else if (start > maxStart)
            {
                start = maxStart;
            }
        }

        _scalarSliceCursor = start + utf8.Length;
        return new Utf8Slice(start, utf8.Length);
    }

    private bool TryResolveNormalizedSlice(ReadOnlySpan<byte> utf8, out int start, out int length)
    {
        start = 0;
        length = 0;

        var source = _source.Span;
        if (utf8.Length == 0 || source.Length < utf8.Length)
        {
            return false;
        }

        var anchorLength = utf8.IndexOf((byte)'\n');
        if (anchorLength < 0)
        {
            anchorLength = utf8.Length;
        }

        if (anchorLength == 0)
        {
            anchorLength = Math.Min(utf8.Length, 32);
        }

        anchorLength = Math.Min(anchorLength, 32);
        var anchor = utf8[..anchorLength];

        if (TryResolveNormalizedSliceFrom(_scalarSliceCursor, source, anchor, utf8, out start, out length))
        {
            return true;
        }

        return _scalarSliceCursor > 0
            && TryResolveNormalizedSliceFrom(0, source, anchor, utf8, out start, out length);
    }

    private bool TryResolveNormalizedSliceFrom(int searchStart, ReadOnlySpan<byte> source, ReadOnlySpan<byte> anchor, ReadOnlySpan<byte> utf8, out int start, out int length)
    {
        start = 0;
        length = 0;

        if (anchor.Length == 0 || searchStart < 0 || searchStart > source.Length - anchor.Length)
        {
            return false;
        }

        var relativeStart = 0;
        var searchSpan = source[searchStart..];
        while (relativeStart <= searchSpan.Length - anchor.Length)
        {
            var next = searchSpan[relativeStart..].IndexOf(anchor);
            if (next < 0)
            {
                return false;
            }

            var candidate = searchStart + relativeStart + next;
            var lineIndentWidth = CountLineIndent(source, candidate);
            if (TryMeasureSourceLength(candidate, utf8, lineIndentWidth, out length))
            {
                start = candidate;
                return true;
            }

            relativeStart += next + 1;
        }

        return false;
    }

    private static int CountLineIndent(ReadOnlySpan<byte> source, int contentStart)
    {
        var lineStart = contentStart;
        while (lineStart > 0)
        {
            var b = source[lineStart - 1];
            if (b is (byte)'\n' or (byte)'\r')
            {
                break;
            }

            lineStart--;
        }

        var indentWidth = 0;
        for (var index = lineStart; index < contentStart; index++)
        {
            var b = source[index];
            if (b is not ((byte)' ' or (byte)'\t'))
            {
                return 0;
            }

            indentWidth++;
        }

        return indentWidth;
    }

    private bool TryMeasureSourceLength(int start, ReadOnlySpan<byte> utf8, int lineIndentWidth, out int length)
    {
        length = 0;

        var source = _source.Span;
        if ((uint)start >= (uint)source.Length)
        {
            return false;
        }

        var sourceIndex = start;
        var atLineStart = false;
        for (var valueIndex = 0; valueIndex < utf8.Length; valueIndex++)
        {
            if (atLineStart)
            {
                var skipped = 0;
                while (skipped < lineIndentWidth
                    && sourceIndex < source.Length
                    && (source[sourceIndex] == (byte)' ' || source[sourceIndex] == (byte)'\t'))
                {
                    sourceIndex++;
                    skipped++;
                }

                atLineStart = false;
            }

            if (sourceIndex >= source.Length)
            {
                return false;
            }

            var valueByte = utf8[valueIndex];
            if (valueByte == (byte)'\n')
            {
                if (source[sourceIndex] == (byte)'\r')
                {
                    if (sourceIndex + 1 >= source.Length || source[sourceIndex + 1] != (byte)'\n')
                    {
                        return false;
                    }

                    sourceIndex += 2;
                    continue;
                }

                if (source[sourceIndex] != (byte)'\n')
                {
                    return false;
                }

                sourceIndex++;
                atLineStart = true;
                continue;
            }

            if (source[sourceIndex] != valueByte)
            {
                return false;
            }

            sourceIndex++;
        }

        length = sourceIndex - start;
        return true;
    }

    private bool TryResolveRawStart(ReadOnlySpan<byte> raw, out int start)
    {
        if (raw.Length == 0)
        {
            start = _scalarSliceCursor <= _source.Length ? _scalarSliceCursor : _source.Length;
            return true;
        }

        var source = _source.Span;
        if (source.Length < raw.Length)
        {
            start = 0;
            return false;
        }

        if (_scalarSliceCursor <= source.Length - raw.Length)
        {
            var offsetFromCursor = source[_scalarSliceCursor..].IndexOf(raw);
            if (offsetFromCursor >= 0)
            {
                start = _scalarSliceCursor + offsetFromCursor;
                return true;
            }
        }

        var offsetFromStart = source.IndexOf(raw);
        if (offsetFromStart >= 0)
        {
            start = offsetFromStart;
            return true;
        }

        start = 0;
        return false;
    }

    public string? GetScalarString() => _parser.GetScalarAsString();

    public ScalarTag GetScalarTag()
    {
        var value = GetScalarUtf8();
        if (value.Length == 0)
        {
            return ScalarTag.Str;
        }

        if (value.SequenceEqual("null"u8) || value.SequenceEqual("~"u8))
        {
            return ScalarTag.Null;
        }

        if (value.SequenceEqual("true"u8) || value.SequenceEqual("false"u8))
        {
            return ScalarTag.Bool;
        }

        if (Utf8Parser.TryParse(value, out long _, out var consumedInt) && consumedInt == value.Length)
        {
            return ScalarTag.Int;
        }

        if (Utf8Parser.TryParse(value, out double _, out var consumedFloat) && consumedFloat == value.Length)
        {
            return ScalarTag.Float;
        }

        return ScalarTag.Str;
    }

    public bool IsScalarQuoted() => false;

    /// <summary>
    /// Converts a UTF-8 byte offset in <see cref="_source"/> to a 1-based line / column position.
    /// Used by the parser core via <see cref="IYamlStreamReader.ComputePositionFromOffset"/> to derive
    /// accurate positions from <see cref="GetScalarSlice"/> offsets, which are more reliable than
    /// VYaml's <see cref="YamlParser.CurrentMark"/> (which advances to the next token for scalars).
    /// </summary>
    public TextPosition ComputePositionFromOffset(int offset)
        => ComputeTextPositionFromOffset(_source.Span, offset);

    /// <summary>
    /// VYaml advances its scanner past an empty scalar to the next meaningful token, so
    /// <see cref="YamlParser.CurrentMark"/> for an empty-scalar event points at that next token.
    /// This helper walks backward through <see cref="_source"/> from <paramref name="nextTokenPosition"/>,
    /// skips whitespace/newlines, and – if it finds an adjacent pair of matching quotes ('''' or &quot;&quot;) –
    /// returns the offset of the opening quote.  Otherwise it returns the backward-walked position.
    /// </summary>
    private int ResolveEmptyScalarStart(int nextTokenPosition)
    {
        var source = _source.Span;
        var pos = nextTokenPosition;
        if (pos > source.Length)
        {
            pos = source.Length;
        }

        // Walk backward past trailing whitespace and line endings.
        while (pos > 0 && source[pos - 1] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
        {
            pos--;
        }

        // If we stopped at a '-' (YAML block sequence indicator), skip over it and continue
        // the backward whitespace scan so the quote check can find e.g. - '' on the prior line.
        if (pos > 0 && source[pos - 1] == (byte)'-')
        {
            pos--;
            while (pos > 0 && source[pos - 1] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
            {
                pos--;
            }
        }

        // Check for '' (two single-quotes) or "" (two double-quotes) immediately before pos.
        if (pos >= 2
            && source[pos - 1] == source[pos - 2]
            && source[pos - 1] is (byte)'\'' or (byte)'"')
        {
            return pos - 2;  // offset of the opening quote
        }

        return pos;
    }

    private static TextPosition ComputeTextPositionFromOffset(ReadOnlySpan<byte> source, int offset)
    {
        var end = offset;
        if (end > source.Length)
        {
            end = source.Length;
        }

        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < end; i++)
        {
            if (source[i] == (byte)'\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return new TextPosition(offset, line, (end - lineStart) + 1);
    }

    private static YamlEventKind MapEventKind(ParseEventType vt)
    {
        return vt switch
        {
            ParseEventType.StreamStart => YamlEventKind.StreamStart,
            ParseEventType.StreamEnd => YamlEventKind.StreamEnd,
            ParseEventType.DocumentStart => YamlEventKind.DocumentStart,
            ParseEventType.DocumentEnd => YamlEventKind.DocumentEnd,
            ParseEventType.MappingStart => YamlEventKind.MappingStart,
            ParseEventType.MappingEnd => YamlEventKind.MappingEnd,
            ParseEventType.SequenceStart => YamlEventKind.SequenceStart,
            ParseEventType.SequenceEnd => YamlEventKind.SequenceEnd,
            ParseEventType.Scalar => YamlEventKind.Scalar,
            ParseEventType.Alias => YamlEventKind.Alias,
            _ => YamlEventKind.None,
        };
    }

    private static ParseEventType MapEventKind(YamlEventKind kind)
    {
        return kind switch
        {
            YamlEventKind.StreamStart => ParseEventType.StreamStart,
            YamlEventKind.StreamEnd => ParseEventType.StreamEnd,
            YamlEventKind.DocumentStart => ParseEventType.DocumentStart,
            YamlEventKind.DocumentEnd => ParseEventType.DocumentEnd,
            YamlEventKind.MappingStart => ParseEventType.MappingStart,
            YamlEventKind.MappingEnd => ParseEventType.MappingEnd,
            YamlEventKind.SequenceStart => ParseEventType.SequenceStart,
            YamlEventKind.SequenceEnd => ParseEventType.SequenceEnd,
            YamlEventKind.Scalar => ParseEventType.Scalar,
            YamlEventKind.Alias => ParseEventType.Alias,
            _ => ParseEventType.Nothing,
        };
    }
}
