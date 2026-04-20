using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

internal static class SpanHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsAsciiWhiteSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsWhiteSpace(byte b) => IsAsciiWhiteSpace(b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte ToLowerAscii(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(value + 32)
            : value;
    }

    internal static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal static string NormalizeAsciiLower(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = (char)value[i];
            if (ch is >= 'A' and <= 'Z')
            {
                ch = (char)(ch + 32);
            }

            buffer[i] = ch;
        }

        return new string(buffer);
    }

    internal static string NormalizeAsciiLower(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var buffer = value.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            var ch = buffer[i];
            if (ch is >= 'A' and <= 'Z')
            {
                buffer[i] = (char)(ch + 32);
            }
        }

        return new string(buffer);
    }

    internal static ReadOnlySpan<byte> TrimAsciiWhiteSpace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length - 1;
        while (start <= end && IsAsciiWhiteSpace(value[start]))
        {
            start++;
        }

        while (end >= start && IsAsciiWhiteSpace(value[end]))
        {
            end--;
        }

        return end < start ? [] : value.Slice(start, end - start + 1);
    }

    internal static Utf8Slice TrimAsciiWhiteSpace(ReadOnlySpan<byte> source, int offset, int length)
    {
        if (length <= 0)
        {
            return new Utf8Slice(offset, 0);
        }

        var start = offset;
        var end = offset + length - 1;

        while (start <= end && IsAsciiWhiteSpace(source[start]))
        {
            start++;
        }

        while (end >= start && IsAsciiWhiteSpace(source[end]))
        {
            end--;
        }

        if (end < start)
        {
            return new Utf8Slice(offset, 0);
        }

        return new Utf8Slice(start, end - start + 1);
    }

    internal static int IndexOf(ReadOnlySpan<byte> source, int start, ReadOnlySpan<byte> pattern)
    {
        if (pattern.IsEmpty || start >= source.Length)
        {
            return -1;
        }

        for (var i = start; i <= source.Length - pattern.Length; i++)
        {
            if (source.Slice(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        return -1;
    }

    internal static (int Line, int Column) ComputeLineColumn(ReadOnlySpan<byte> source, int offset)
    {
        var line = 1;
        var lineStart = 0;
        var end = offset;
        if (end >= source.Length)
        {
            end = source.Length - 1;
        }

        for (var i = 0; i < end; i++)
        {
            if (source[i] == (byte)'\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, (end - lineStart) + 1);
    }
}
