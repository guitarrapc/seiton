using VYaml.Parser;

namespace Seiton.Core.Parsing;

internal ref struct VYamlStreamReader
{
    private YamlParser _parser;

    public VYamlStreamReader(Memory<byte> bytes)
    {
        _parser = YamlParser.FromBytes(bytes);
    }

    public ParseEventType CurrentEventType => _parser.CurrentEventType;

    public bool End => _parser.End;

    public Marker CurrentMark => _parser.CurrentMark;

    public bool Read() => _parser.Read();

    public void SkipHeader() => _parser.SkipAfter(ParseEventType.DocumentStart);

    public void SkipCurrentNode() => _parser.SkipCurrentNode();

    public ReadOnlySpan<byte> GetScalarUtf8() => _parser.GetScalarAsUtf8();

    public string? GetScalarString() => _parser.GetScalarAsString();

    public Utf8Slice GetScalarSlice()
    {
        var mark = _parser.CurrentMark;
        var utf8 = _parser.GetScalarAsUtf8();
        return new Utf8Slice(mark.Position, utf8.Length);
    }
}
