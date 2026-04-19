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
        if (_parser.TryGetScalarAsSpan(out var raw) && TryResolveRawStart(raw, out var rawStart))
        {
            _scalarSliceCursor = rawStart + raw.Length;
            return new Utf8Slice(rawStart, raw.Length);
        }

        var utf8 = _parser.GetScalarAsUtf8();
        if (utf8.Length == 0)
        {
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
