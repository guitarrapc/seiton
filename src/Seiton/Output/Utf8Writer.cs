using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;

namespace Seiton.Output;

/// <summary>
/// UTF-8 output over <see cref="IBufferWriter{Byte}"/>. Used by diagnostic formatting and CLI stdout.
/// </summary>
internal ref struct Utf8Writer
{
    private const int RepeatedByteStackLimit = 128;
    private const int StackUtf8Limit = 512;

    private static readonly byte[] PlatformNewLine = Encoding.UTF8.GetBytes(Environment.NewLine);

    private IBufferWriter<byte> _destination;

    public Utf8Writer(IBufferWriter<byte> destination) => _destination = destination;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLiteral(ReadOnlySpan<byte> utf8) => WriteLiteralCore(_destination, utf8);

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
    public void Write(string value) => WriteUtf8(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(char value)
    {
        Span<byte> scratch = stackalloc byte[4];
        Span<char> chars = stackalloc char[1];
        chars[0] = value;
        var written = Encoding.UTF8.GetBytes(chars, scratch);
        WriteLiteralCore(_destination, scratch[..written]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(int value) => WriteInt(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine() => WriteNewLine();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(string value)
    {
        WriteUtf8(value);
        WriteNewLine();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLine(int value)
    {
        WriteInt(value);
        WriteNewLine();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        var span = _destination.GetSpan(1);
        span[0] = value;
        _destination.Advance(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteNewLine() => WriteLiteral(PlatformNewLine);

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

    public void WriteRepeated(byte value, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var remaining = count;
        while (remaining > 0)
        {
            var span = _destination.GetSpan(Math.Min(remaining, RepeatedByteStackLimit));
            if (span.IsEmpty)
            {
                throw new InvalidOperationException("IBufferWriter returned an empty span.");
            }

            var chunk = Math.Min(span.Length, remaining);
            span[..chunk].Fill(value);
            _destination.Advance(chunk);
            remaining -= chunk;
        }
    }

    public void WritePaddedDecimal(int value, int minWidth)
    {
        Span<byte> buffer = stackalloc byte[16];
        if (!Utf8Formatter.TryFormat(value, buffer, out var written))
        {
            WriteUtf8(value.ToString());
            return;
        }

        WriteRepeated((byte)' ', minWidth - written);
        WriteLiteralCore(_destination, buffer[..written]);
    }

    public static void WriteToStandardOutput(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return;
        }

        using var stream = Console.OpenStandardOutput();
        stream.Write(utf8);
    }

    public static void WriteToStandardError(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return;
        }

        using var stream = Console.OpenStandardError();
        stream.Write(utf8);
    }

    public static void WriteToTextWriter(TextWriter writer, ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return;
        }

        var charCount = Encoding.UTF8.GetCharCount(utf8);
        if (charCount <= 2048)
        {
            Span<char> chars = stackalloc char[charCount];
            var written = Encoding.UTF8.GetChars(utf8, chars);
            writer.Write(chars[..written]);
            return;
        }

        var rented = ArrayPool<char>.Shared.Rent(charCount);
        try
        {
            var written = Encoding.UTF8.GetChars(utf8, rented);
            writer.Write(rented, 0, written);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }
}
