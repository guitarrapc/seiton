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

        if (source.Length == 0 || diagnostic.Location.StartLine <= 0)
        {
            return false;
        }

        var targetLine = diagnostic.Location.StartLine - 1;
        if ((uint)targetLine >= (uint)lineIndex.Count)
        {
            return false;
        }

        if (!ShouldAttempt(diagnostic.Message, lineIndex, targetLine))
        {
            return false;
        }

        var chain = BuildAncestorChain(lineIndex, targetLine);
        if (chain.Length < 2)
        {
            return false;
        }

        chain = TrimChainStart(chain, lineIndex);
        if (chain.Length < 2)
        {
            return false;
        }

        return TryBuildDisplayLines(lineIndex, chain, out lines);
    }

    private static bool ShouldAttempt(string message, YamlLineIndex lineIndex, int targetLine)
    {
        if (HasMessagePathPrefix(message))
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

    private static int[] BuildAncestorChain(YamlLineIndex lineIndex, int targetLine)
    {
        int[]? rented = null;
        var buffer = lineIndex.Count <= MaxStackLines
            ? stackalloc int[lineIndex.Count]
            : (rented = ArrayPool<int>.Shared.Rent(lineIndex.Count)).AsSpan(0, lineIndex.Count);

        try
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
            return buffer[..count].ToArray();
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    private static int[] TrimChainStart(int[] chain, YamlLineIndex lineIndex)
    {
        for (var i = 0; i < chain.Length; i++)
        {
            if (lineIndex.IsJobsKey(chain[i]))
            {
                if (i == 0)
                {
                    return chain;
                }

                var trimmed = new int[chain.Length - i];
                Array.Copy(chain, i, trimmed, 0, trimmed.Length);
                return trimmed;
            }
        }

        for (var i = 0; i < chain.Length; i++)
        {
            if (lineIndex.IsRunsKey(chain[i]))
            {
                if (i == 0)
                {
                    return chain;
                }

                var trimmed = new int[chain.Length - i];
                Array.Copy(chain, i, trimmed, 0, trimmed.Length);
                return trimmed;
            }
        }

        return chain;
    }

    private static bool TryBuildDisplayLines(YamlLineIndex lineIndex, int[] chain, out StructureSnippetLines lines)
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

        lines = new StructureSnippetLines(entries);
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
    public StructureSnippetLines(StructureSnippetEntry[] entries) => Entries = entries;

    public StructureSnippetEntry[] Entries { get; }
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
