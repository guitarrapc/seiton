using System.Buffers;
using System.Runtime.CompilerServices;
using Seiton.Core.Parsing;

namespace Seiton.Output;

internal static class StructureSnippetBuilder
{
    private const int MaxStackLines = 256;

    public static bool TryBuild(
        byte[] source,
        Diagnostic diagnostic,
        YamlLineIndex? cachedIndex,
        out YamlLineIndex lineIndex,
        out StructureSnippetLines lines)
    {
        lineIndex = cachedIndex ?? YamlLineIndex.Create(source);
        lines = default;

        if (source.Length == 0)
        {
            return false;
        }

        var structurePath = default(DiagnosticStructurePath);
        var hasStructurePath = DiagnosticStructurePathParser.TryParse(diagnostic, out structurePath);
        var targetLine = diagnostic.Location.StartLine - 1;
        if (hasStructurePath
            && DiagnosticStructurePathResolver.TryResolveTargetLine(lineIndex, structurePath, out var resolvedLine))
        {
            targetLine = resolvedLine;
        }
        else if ((uint)targetLine >= (uint)lineIndex.Count)
        {
            return false;
        }

        if (!ShouldAttempt(diagnostic, structurePath, lineIndex, targetLine))
        {
            return false;
        }

        int[]? rentedChain = null;
        var chainBuffer = lineIndex.Count <= MaxStackLines
            ? stackalloc int[lineIndex.Count]
            : (rentedChain = ArrayPool<int>.Shared.Rent(lineIndex.Count)).AsSpan(0, lineIndex.Count);

        try
        {
            var chainLength = BuildAncestorChain(lineIndex, targetLine, chainBuffer);
            if (chainLength < 2)
            {
                return false;
            }

            var trimStart = FindTrimStart(chainBuffer, chainLength, lineIndex);
            var trimmedLength = chainLength - trimStart;
            if (trimmedLength < 2)
            {
                return false;
            }

            return TryBuildDisplayLines(lineIndex, chainBuffer.Slice(trimStart, trimmedLength), targetLine, out lines);
        }
        finally
        {
            if (rentedChain is not null)
            {
                ArrayPool<int>.Shared.Return(rentedChain);
            }
        }
    }

    private static bool ShouldAttempt(
        Diagnostic diagnostic,
        DiagnosticStructurePath structurePath,
        YamlLineIndex lineIndex,
        int targetLine)
    {
        if (structurePath.IsWorkflowScoped)
        {
            return true;
        }

        if (HasMessagePathPrefix(diagnostic.Message))
        {
            return true;
        }

        return HasStructuralAncestor(lineIndex, targetLine);
    }

    private static bool HasMessagePathPrefix(string message)
    {
        return message.StartsWith("jobs.", StringComparison.Ordinal)
            || message.StartsWith("steps[", StringComparison.Ordinal);
    }

    private static bool HasStructuralAncestor(YamlLineIndex lineIndex, int targetLine)
    {
        var current = targetLine;
        var currentIndent = lineIndex.GetIndent(current);

        while (current > 0)
        {
            var parent = FindParentLine(lineIndex, current, currentIndent);
            if (parent < 0)
            {
                break;
            }

            if (lineIndex.IsStructuralKey(parent))
            {
                return true;
            }

            current = parent;
            currentIndent = lineIndex.GetIndent(parent);
        }

        return false;
    }

    private static int BuildAncestorChain(YamlLineIndex lineIndex, int targetLine, Span<int> buffer)
    {
        var count = 0;
        var current = targetLine;
        var currentIndent = lineIndex.GetIndent(current);

        while (current >= 0 && count < buffer.Length)
        {
            buffer[count++] = current;
            var parent = FindParentLine(lineIndex, current, currentIndent);
            if (parent < 0)
            {
                break;
            }

            current = parent;
            currentIndent = lineIndex.GetIndent(parent);
        }

        buffer[..count].Reverse();
        return count;
    }

    private static int FindTrimStart(ReadOnlySpan<int> chain, int length, YamlLineIndex lineIndex)
    {
        for (var i = 0; i < length; i++)
        {
            if (lineIndex.IsJobsKey(chain[i]))
            {
                return i;
            }
        }

        for (var i = 0; i < length; i++)
        {
            if (lineIndex.IsRunsKey(chain[i]))
            {
                return i;
            }
        }

        return 0;
    }

    private static bool TryBuildDisplayLines(
        YamlLineIndex lineIndex,
        ReadOnlySpan<int> chain,
        int targetLine0,
        out StructureSnippetLines lines)
    {
        var displayCount = chain.Length;
        for (var i = 0; i < chain.Length - 1; i++)
        {
            if (chain[i + 1] - chain[i] > 1)
            {
                displayCount++;
            }
        }

        var entries = new StructureSnippetEntry[displayCount];
        var entryIndex = 0;

        for (var i = 0; i < chain.Length; i++)
        {
            if (i > 0 && chain[i] - chain[i - 1] > 1)
            {
                entries[entryIndex++] = StructureSnippetEntry.Ellipsis;
            }

            entries[entryIndex++] = new StructureSnippetEntry(chain[i] + 1, lineIndex.GetLineUtf8(chain[i]));
        }

        lines = new StructureSnippetLines(entries, targetLine0 + 1);
        return true;
    }

    private static int FindParentLine(YamlLineIndex lineIndex, int lineIndex0, int indent)
    {
        for (var i = lineIndex0 - 1; i >= 0; i--)
        {
            if (lineIndex.IsBlank(i))
            {
                continue;
            }

            if (lineIndex.GetIndent(i) < indent)
            {
                return i;
            }
        }

        return -1;
    }
}

internal readonly struct StructureSnippetEntry
{
    public static StructureSnippetEntry Ellipsis { get; } = new(-1, default);

    public StructureSnippetEntry(int lineNumber, ReadOnlyMemory<byte> lineUtf8)
    {
        LineNumber = lineNumber;
        LineUtf8 = lineUtf8;
    }

    public int LineNumber { get; }
    public ReadOnlyMemory<byte> LineUtf8 { get; }
    public bool IsEllipsis => LineNumber < 0;
}

internal readonly struct StructureSnippetLines
{
    public StructureSnippetLines(StructureSnippetEntry[] entries, int highlightLine1Based)
    {
        Entries = entries;
        HighlightLine1Based = highlightLine1Based;
    }

    public StructureSnippetEntry[] Entries { get; }
    public int HighlightLine1Based { get; }
    public bool IsEmpty => Entries.Length == 0;
}

internal sealed class YamlLineIndex
{
    private readonly byte[] _source;
    private readonly int[] _lineStarts;
    private readonly int[] _lineLengths;
    private readonly int[] _indents;

    private YamlLineIndex(byte[] source, int[] lineStarts, int[] lineLengths, int[] indents)
    {
        _source = source;
        _lineStarts = lineStarts;
        _lineLengths = lineLengths;
        _indents = indents;
        Count = lineStarts.Length;
    }

    public int Count { get; }

    public static YamlLineIndex Create(byte[] source)
    {
        if (source.Length == 0)
        {
            return new YamlLineIndex(source, [], [], []);
        }

        var lineCount = 1;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == (byte)'\n')
            {
                lineCount++;
            }
        }

        var lineStarts = new int[lineCount];
        var lineLengths = new int[lineCount];
        var indents = new int[lineCount];

        var lineIndex = 0;
        var lineStart = 0;
        for (var i = 0; i <= source.Length; i++)
        {
            var isEnd = i == source.Length;
            var isNewline = !isEnd && source[i] == (byte)'\n';
            if (!isNewline && !isEnd)
            {
                continue;
            }

            var length = i - lineStart;
            if (length > 0 && source[lineStart + length - 1] == (byte)'\r')
            {
                length--;
            }

            lineStarts[lineIndex] = lineStart;
            lineLengths[lineIndex] = length;
            indents[lineIndex] = ComputeIndent(source, lineStart, length);
            lineIndex++;
            lineStart = i + 1;
        }

        return new YamlLineIndex(source, lineStarts, lineLengths, indents);
    }

    public int GetIndent(int lineIndex)
        => (uint)lineIndex < (uint)Count ? _indents[lineIndex] : 0;

    public bool IsBlank(int lineIndex)
        => (uint)lineIndex < (uint)Count && _lineLengths[lineIndex] == 0;

    public ReadOnlyMemory<byte> GetLineUtf8(int lineIndex)
    {
        if ((uint)lineIndex >= (uint)Count)
        {
            return default;
        }

        return _source.AsMemory(_lineStarts[lineIndex], _lineLengths[lineIndex]);
    }

    public bool IsStructuralKey(int lineIndex)
        => IsJobsKey(lineIndex) || IsStepsKey(lineIndex) || IsRunsKey(lineIndex);

    public bool IsJobsKey(int lineIndex)
        => StartsWithTrimmedKey(lineIndex, "jobs:"u8);

    public bool IsStepsKey(int lineIndex)
        => StartsWithTrimmedKey(lineIndex, "steps:"u8);

    public bool IsRunsKey(int lineIndex)
        => StartsWithTrimmedKey(lineIndex, "runs:"u8);

    public bool TryFindJobsLine(out int line0)
    {
        for (var i = 0; i < Count; i++)
        {
            if (IsJobsKey(i))
            {
                line0 = i;
                return true;
            }
        }

        line0 = -1;
        return false;
    }

    public bool TryFindRunsLine(out int line0)
    {
        for (var i = 0; i < Count; i++)
        {
            if (IsRunsKey(i))
            {
                line0 = i;
                return true;
            }
        }

        line0 = -1;
        return false;
    }

    public bool TryFindChildMappingKey(int parentLine, string key, out int line0)
    {
        return TryFindChildScalarKey(parentLine, key.AsSpan(), out line0);
    }

    public bool TryFindChildScalarKey(int parentLine, ReadOnlySpan<char> key, out int line0)
    {
        line0 = -1;
        if ((uint)parentLine >= (uint)Count || key.IsEmpty)
        {
            return false;
        }

        var parentIndent = _indents[parentLine];
        var expectedIndent = parentIndent + 2;

        if (key.Length <= 64)
        {
            Span<byte> keyUtf8 = stackalloc byte[key.Length];
            var count = System.Text.Encoding.UTF8.GetBytes(key, keyUtf8);
            return TryFindChildScalarKeyUtf8(parentLine, parentIndent, expectedIndent, keyUtf8[..count], out line0);
        }

        var rented = ArrayPool<byte>.Shared.Rent(key.Length);
        try
        {
            var written = System.Text.Encoding.UTF8.GetBytes(key, rented);
            return TryFindChildScalarKeyUtf8(parentLine, parentIndent, expectedIndent, rented.AsSpan(0, written), out line0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private bool TryFindChildScalarKeyUtf8(
        int parentLine,
        int parentIndent,
        int expectedIndent,
        ReadOnlySpan<byte> keyUtf8,
        out int line0)
    {
        line0 = -1;
        for (var i = parentLine + 1; i < Count; i++)
        {
            if (IsBlank(i))
            {
                continue;
            }

            var indent = _indents[i];
            if (indent <= parentIndent)
            {
                break;
            }

            if (indent != expectedIndent)
            {
                continue;
            }

            if (LineKeyEquals(i, keyUtf8))
            {
                line0 = i;
                return true;
            }
        }

        return false;
    }

    public bool TryFindSequenceItemLine(int sequenceKeyLine, int index1Based, out int line0)
    {
        line0 = -1;
        if ((uint)sequenceKeyLine >= (uint)Count || index1Based <= 0)
        {
            return false;
        }

        var parentIndent = _indents[sequenceKeyLine];
        var itemIndent = parentIndent + 2;
        var itemIndex = 0;

        for (var i = sequenceKeyLine + 1; i < Count; i++)
        {
            if (IsBlank(i))
            {
                continue;
            }

            var indent = _indents[i];
            if (indent <= parentIndent)
            {
                break;
            }

            if (indent != itemIndent)
            {
                continue;
            }

            if (!IsSequenceItemLine(i))
            {
                continue;
            }

            itemIndex++;
            if (itemIndex == index1Based)
            {
                line0 = i;
                return true;
            }
        }

        return false;
    }

    private bool IsSequenceItemLine(int lineIndex)
    {
        if ((uint)lineIndex >= (uint)Count)
        {
            return false;
        }

        var span = _source.AsSpan(_lineStarts[lineIndex], _lineLengths[lineIndex]);
        var start = _indents[lineIndex];
        if ((uint)start >= (uint)span.Length)
        {
            return false;
        }

        span = span[start..];
        return span.Length >= 2 && span[0] == (byte)'-' && span[1] == (byte)' ';
    }

    public bool IsSequenceItemWithInlineKey(int lineIndex, ReadOnlySpan<char> key)
    {
        if ((uint)lineIndex >= (uint)Count || key.IsEmpty)
        {
            return false;
        }

        if (key.Length <= 16)
        {
            Span<byte> keyUtf8 = stackalloc byte[key.Length];
            var written = System.Text.Encoding.UTF8.GetBytes(key, keyUtf8);
            return IsSequenceItemWithInlineKeyUtf8(lineIndex, keyUtf8[..written]);
        }

        var rented = ArrayPool<byte>.Shared.Rent(key.Length);
        try
        {
            var written = System.Text.Encoding.UTF8.GetBytes(key, rented);
            return IsSequenceItemWithInlineKeyUtf8(lineIndex, rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private bool IsSequenceItemWithInlineKeyUtf8(int lineIndex, ReadOnlySpan<byte> keyUtf8)
    {
        if (!IsSequenceItemLine(lineIndex))
        {
            return false;
        }

        var span = _source.AsSpan(_lineStarts[lineIndex], _lineLengths[lineIndex]);
        var start = _indents[lineIndex];
        if ((uint)start >= (uint)span.Length)
        {
            return false;
        }

        span = span[start..];
        if (span.Length < 2 || span[0] != (byte)'-' || span[1] != (byte)' ')
        {
            return false;
        }

        span = span[2..];
        if (span.Length < keyUtf8.Length + 1 || span[keyUtf8.Length] != (byte)':')
        {
            return false;
        }

        return span[..keyUtf8.Length].SequenceEqual(keyUtf8);
    }

    private bool LineKeyEquals(int lineIndex, ReadOnlySpan<byte> keyUtf8)
    {
        var span = _source.AsSpan(_lineStarts[lineIndex], _lineLengths[lineIndex]);
        var start = _indents[lineIndex];
        if ((uint)start >= (uint)span.Length)
        {
            return false;
        }

        span = span[start..];
        if (span.Length < keyUtf8.Length + 1 || span[keyUtf8.Length] != (byte)':')
        {
            return false;
        }

        return span[..keyUtf8.Length].SequenceEqual(keyUtf8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool StartsWithTrimmedKey(int lineIndex, ReadOnlySpan<byte> key)
    {
        if ((uint)lineIndex >= (uint)Count)
        {
            return false;
        }

        var span = _source.AsSpan(_lineStarts[lineIndex], _lineLengths[lineIndex]);
        var start = _indents[lineIndex];
        if ((uint)start >= (uint)span.Length)
        {
            return false;
        }

        span = span[start..];
        return span.StartsWith(key);
    }

    private static int ComputeIndent(ReadOnlySpan<byte> source, int lineStart, int length)
    {
        var indent = 0;
        var end = lineStart + length;
        for (var i = lineStart; i < end; i++)
        {
            var b = source[i];
            if (b is (byte)' ' or (byte)'\t')
            {
                indent++;
                continue;
            }

            break;
        }

        return indent;
    }
}
