using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

internal sealed class FakeYamlStreamReader : IYamlStreamReader
{
    private readonly FakeEvent[] _events;
    private readonly byte[] _source;
    private int _index;

    public FakeYamlStreamReader(FakeEvent[] events, byte[] source)
    {
        _events = events;
        _source = source;
        _index = events.Length == 0 ? -1 : 0;
    }

    public YamlEventKind CurrentKind => IsValidIndex ? _events[_index].Kind : YamlEventKind.None;

    public bool End => !IsValidIndex;

    public TextPosition CurrentStart => IsValidIndex ? _events[_index].Start : default;

    public TextPosition CurrentEnd => IsValidIndex ? _events[_index].End : default;

    public bool Read()
    {
        if (!IsValidIndex)
        {
            return false;
        }

        _index++;
        if (_index >= _events.Length)
        {
            _index = -1;
            return false;
        }

        return true;
    }

    public void SkipHeader()
    {
        while (IsValidIndex && CurrentKind is not YamlEventKind.DocumentStart and not YamlEventKind.MappingStart)
        {
            Read();
        }

        if (CurrentKind == YamlEventKind.DocumentStart)
        {
            Read();
        }
    }

    public void SkipCurrentNode()
    {
        if (!IsValidIndex)
        {
            return;
        }

        var kind = CurrentKind;
        if (kind is not YamlEventKind.MappingStart and not YamlEventKind.SequenceStart)
        {
            Read();
            return;
        }

        var depth = 0;
        while (IsValidIndex)
        {
            if (CurrentKind is YamlEventKind.MappingStart or YamlEventKind.SequenceStart)
            {
                depth++;
            }
            else if (CurrentKind is YamlEventKind.MappingEnd or YamlEventKind.SequenceEnd)
            {
                depth--;
                if (depth == 0)
                {
                    Read();
                    break;
                }
            }

            Read();
        }
    }

    public void SkipAfter(YamlEventKind kind)
    {
        while (IsValidIndex && CurrentKind != kind)
        {
            Read();
        }

        if (CurrentKind == kind)
        {
            Read();
        }
    }

    public ReadOnlySpan<byte> GetScalarUtf8()
    {
        if (!IsValidIndex)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        var e = _events[_index];
        if (e.Slice.Length == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        return e.Slice.AsSpan(_source);
    }

    public Utf8Slice GetScalarSlice() => IsValidIndex ? _events[_index].Slice : default;

    public string? GetScalarString()
    {
        var utf8 = GetScalarUtf8();
        return utf8.IsEmpty ? string.Empty : System.Text.Encoding.UTF8.GetString(utf8);
    }

    public ScalarTag GetScalarTag() => IsValidIndex ? _events[_index].Tag : ScalarTag.Unknown;

    public bool IsScalarQuoted() => IsValidIndex && _events[_index].Quoted;

    public bool IsExplicitNull() => IsValidIndex && _events[_index].Tag == ScalarTag.Null && _events[_index].ExplicitNull;

    public TextPosition ComputePositionFromOffset(int offset)
    {
        // FakeYamlStreamReader events already carry correct positions, but honour the same
        // line/column computation logic that VYamlStreamAdapter uses so tests are consistent.
        var source = _source.AsSpan();
        var end = offset;
        if (end > source.Length) end = source.Length;

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

    private bool IsValidIndex => _index >= 0 && _index < _events.Length;

    internal readonly record struct FakeEvent(
        YamlEventKind Kind,
        Utf8Slice Slice,
        TextPosition Start,
        TextPosition End,
        ScalarTag Tag = ScalarTag.Unknown,
        bool Quoted = false,
        bool ExplicitNull = false);
}
