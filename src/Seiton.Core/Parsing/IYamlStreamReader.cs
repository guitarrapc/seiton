namespace Seiton.Core.Parsing;

/// <summary>Pull-based YAML event reader abstraction for the workflow parser.</summary>
public interface IYamlStreamReader
{
    /// <summary>Gets the kind of the current YAML event.</summary>
    YamlEventKind CurrentKind { get; }

    /// <summary>Gets whether all events have been consumed.</summary>
    bool End { get; }

    /// <summary>Advances to the next YAML event. Returns <c>false</c> when the stream is exhausted.</summary>
    bool Read();

    /// <summary>Skips document header events (StreamStart, DocumentStart) to position at the first content event.</summary>
    void SkipHeader();

    /// <summary>Skips the current node and all of its children.</summary>
    void SkipCurrentNode();

    /// <summary>Reads and discards events until a matching <paramref name="kind"/> end event is consumed.</summary>
    void SkipAfter(YamlEventKind kind);

    /// <summary>Returns the current scalar value as a UTF-8 byte span. Valid only when <see cref="CurrentKind"/> is <see cref="YamlEventKind.Scalar"/>.</summary>
    ReadOnlySpan<byte> GetScalarUtf8();

    /// <summary>Returns the current scalar value as a zero-copy <see cref="Utf8Slice"/> into the source bytes.</summary>
    Utf8Slice GetScalarSlice();

    /// <summary>Returns the current scalar value decoded as a <see cref="string"/>, or <c>null</c> if empty.</summary>
    string? GetScalarString();

    /// <summary>Returns the YAML tag of the current scalar (e.g. <c>!!null</c>, <c>!!str</c>).</summary>
    ScalarTag GetScalarTag();

    /// <summary>Returns whether the current scalar is quoted (single or double).</summary>
    bool IsScalarQuoted();

    /// <summary>
    /// Returns <c>true</c> when the current scalar is an explicit YAML null literal
    /// (<c>null</c> or <c>~</c>), as opposed to an implicit empty value (e.g. <c>key:</c>).
    /// Both cases have <see cref="GetScalarTag"/> == <see cref="ScalarTag.Null"/>,
    /// but this method distinguishes them by checking whether source bytes are present.
    /// </summary>
    bool IsExplicitNull();

    /// <summary>Gets the start position of the current event.</summary>
    TextPosition CurrentStart { get; }

    /// <summary>Gets the end position of the current event.</summary>
    TextPosition CurrentEnd { get; }

    /// <summary>
    /// Converts a UTF-8 byte offset in the underlying source to a 1-based line/column <see cref="TextPosition"/>.
    /// Prefer this over <see cref="CurrentStart"/> when the reliable offset is already known (e.g. from
    /// <see cref="GetScalarSlice"/>), because some YAML adapters advance their internal mark past the
    /// current token before the parser can inspect it.
    /// </summary>
    TextPosition ComputePositionFromOffset(int offset);
}
