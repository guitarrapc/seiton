using System.Buffers.Text;
using VYaml.Parser;

namespace Seiton.Core.Parsing;

internal ref struct VYamlStreamAdapter : IYamlStreamReader
{
    private YamlParser _parser;

    public VYamlStreamAdapter(Memory<byte> bytes)
    {
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
        var mark = _parser.CurrentMark;
        var utf8 = _parser.GetScalarAsUtf8();
        return new Utf8Slice(mark.Position, utf8.Length);
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
