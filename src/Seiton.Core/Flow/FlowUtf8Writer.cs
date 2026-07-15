using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;

namespace Seiton.Core.Flow;

/// <summary>UTF-8 writer over <see cref="IBufferWriter{Byte}"/> for flow serializers.</summary>
internal struct FlowUtf8Writer
{
    private const int StackUtf8Limit = 512;

    private IBufferWriter<byte> _destination;

    public FlowUtf8Writer(IBufferWriter<byte> destination) => _destination = destination;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLiteral(ReadOnlySpan<byte> utf8) => WriteLiteralCore(_destination, utf8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteAscii(ReadOnlySpan<char> ascii)
    {
        for (var i = 0; i < ascii.Length; i++)
        {
            var c = ascii[i];
            if (c > 0x7F)
            {
                throw new ArgumentOutOfRangeException(nameof(ascii), "ASCII-only span required.");
            }

            WriteByte((byte)c);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteAscii(string ascii) => WriteAscii(ascii.AsSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        var span = _destination.GetSpan(1);
        span[0] = value;
        _destination.Advance(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUtf8(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
        {
            return;
        }

        var maxByteCount = Encoding.UTF8.GetMaxByteCount(chars.Length);
        if (maxByteCount <= StackUtf8Limit)
        {
            Span<byte> scratch = stackalloc byte[StackUtf8Limit];
            var written = Encoding.UTF8.GetBytes(chars, scratch);
            WriteLiteralCore(_destination, scratch[..written]);
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(chars);
        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(chars, rented.AsSpan(0, byteCount));
            WriteLiteral(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUtf8(string value) => WriteUtf8(value.AsSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUtf8Bytes(ReadOnlySpan<byte> value) => WriteLiteralCore(_destination, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteAscii(char c)
    {
        if (c > 0x7F)
        {
            throw new ArgumentOutOfRangeException(nameof(c), "ASCII character required.");
        }

        WriteByte((byte)c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt(int value)
    {
        Span<byte> buffer = stackalloc byte[16];
        if (Utf8Formatter.TryFormat(value, buffer, out var written))
        {
            WriteLiteralCore(_destination, buffer[..written]);
            return;
        }

        WriteUtf8(value.ToString());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteNewLine() => WriteLiteral("\n"u8);

    private static void WriteLiteralCore(IBufferWriter<byte> destination, ReadOnlySpan<byte> utf8)
    {
        var offset = 0;
        while (offset < utf8.Length)
        {
            var span = destination.GetSpan(Math.Min(utf8.Length - offset, 4096));
            if (span.IsEmpty)
            {
                throw new InvalidOperationException("IBufferWriter returned an empty span.");
            }

            var chunk = Math.Min(span.Length, utf8.Length - offset);
            utf8.Slice(offset, chunk).CopyTo(span);
            destination.Advance(chunk);
            offset += chunk;
        }
    }
}
