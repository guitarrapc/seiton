using System.Runtime.CompilerServices;
using System.Text;
using static Seiton.Core.Parsing.SpanHelpers;

namespace Seiton.Core.Parsing;

/// <summary>Byte-level scanning utilities for locating <c>${{ ... }}</c> expression boundaries in UTF-8 YAML.</summary>
internal static class ExpressionScanHelpers
{
    internal static bool TryFindExpression(
        ReadOnlySpan<byte> value,
        int searchStart,
        out int bodyStart,
        out int bodyLength,
        out int nextSearchStart)
    {
        bodyStart = 0;
        bodyLength = 0;
        nextSearchStart = 0;

        if ((uint)searchStart >= (uint)value.Length)
        {
            return false;
        }

        var start = value[searchStart..].IndexOf("${{"u8);
        if (start < 0)
        {
            return false;
        }

        bodyStart = searchStart + start + 3;
        var close = value[bodyStart..].IndexOf("}}"u8);
        if (close < 0)
        {
            return false;
        }

        bodyLength = close;
        nextSearchStart = bodyStart + close + 2;
        return true;
    }

    internal static int[] BuildLineStarts(byte[] source)
    {
        var starts = new PooledBuffer<int>(source.Length / 20 + 16);
        try
        {
            starts.Add(0);
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == (byte)'\n')
                {
                    var next = i + 1;
                    if (next < source.Length)
                    {
                        starts.Add(next);
                    }
                }
            }

            return starts.ToArray();
        }
        finally
        {
            starts.Dispose();
        }
    }

    internal static (int Line, int Column) OffsetToLineColumn(int[] lineStarts, int offset)
    {
        var idx = Array.BinarySearch(lineStarts, offset);
        if (idx >= 0)
        {
            return (idx + 1, 1);
        }

        idx = ~idx - 1;
        if (idx < 0)
        {
            return (1, offset + 1);
        }

        return (idx + 1, offset - lineStarts[idx] + 1);
    }

    internal static bool IsContextRootIdentifier(int nodeId, int parentId, ReadOnlySpan<ExpressionNode> nodes)
    {
        if (parentId < 0)
        {
            return true;
        }

        if (parentId >= nodes.Length)
        {
            return false;
        }

        var parent = nodes[parentId];
        return parent.Left == nodeId
            && (parent.Kind == ExpressionNodeKind.MemberAccess
                || parent.Kind == ExpressionNodeKind.IndexAccess
                || parent.Kind == ExpressionNodeKind.WildcardAccess);
    }

    internal static bool ConsumeWordIgnoreCase(ReadOnlySpan<byte> value, ref int index, ReadOnlySpan<byte> word)
    {
        if (index + word.Length > value.Length)
        {
            return false;
        }

        for (var i = 0; i < word.Length; i++)
        {
            var l = value[index + i];
            var r = word[i];
            if (l is >= (byte)'A' and <= (byte)'Z')
            {
                l = (byte)(l + 32);
            }

            if (l != r)
            {
                return false;
            }
        }

        index += word.Length;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SkipWhiteSpace(ReadOnlySpan<byte> value, ref int index)
    {
        while (index < value.Length && IsWhiteSpace(value[index]))
        {
            index++;
        }
    }

    internal static bool TryReadIdentifier(ReadOnlySpan<byte> value, ref int index, out string identifier)
    {
        identifier = string.Empty;
        if (index >= value.Length || !IsIdentifierStart(value[index]))
        {
            return false;
        }

        var start = index;
        index++;
        while (index < value.Length && IsIdentifierPart(value[index]))
        {
            index++;
        }

        identifier = Encoding.UTF8.GetString(value[start..index]);
        return true;
    }

    internal static bool IsSimpleIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (!IsIdentifierStart((byte)value[0]))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (!IsIdentifierPart((byte)value[i]))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsIdentifierStart(byte b)
    {
        return (b >= (byte)'A' && b <= (byte)'Z')
            || (b >= (byte)'a' && b <= (byte)'z')
            || b == (byte)'_';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsIdentifierPart(byte b)
    {
        return IsIdentifierStart(b) || (b >= (byte)'0' && b <= (byte)'9');
    }
}
