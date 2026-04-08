namespace Seiton.Core.Parsing;

public interface IYamlStreamReader
{
    YamlEventKind CurrentKind { get; }

    bool End { get; }

    bool Read();

    void SkipHeader();

    void SkipCurrentNode();

    void SkipAfter(YamlEventKind kind);

    ReadOnlySpan<byte> GetScalarUtf8();

    Utf8Slice GetScalarSlice();

    string? GetScalarString();

    ScalarTag GetScalarTag();

    bool IsScalarQuoted();

    TextPosition CurrentStart { get; }

    TextPosition CurrentEnd { get; }
}
