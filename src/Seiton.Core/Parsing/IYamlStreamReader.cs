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

    /// <summary>
    /// Converts a UTF-8 byte offset in the underlying source to a 1-based line/column <see cref="TextPosition"/>.
    /// Prefer this over <see cref="CurrentStart"/> when the reliable offset is already known (e.g. from
    /// <see cref="GetScalarSlice"/>), because some YAML adapters advance their internal mark past the
    /// current token before the parser can inspect it.
    /// </summary>
    TextPosition ComputePositionFromOffset(int offset);
}
