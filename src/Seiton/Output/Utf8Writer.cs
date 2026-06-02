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

    private static readonly byte[] PlatformNewLine = Encoding.UTF8.GetBytes(Environment.NewLine);

    private IBufferWriter<byte> _destination;

    public Utf8Writer(IBufferWriter<byte> destination) => _destination = destination;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLiteral(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return;
        }

        var span = _destination.GetSpan(utf8.Length);
        utf8.CopyTo(span);
        _destination.Advance(utf8.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUtf8(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
        {
            return;
        }

        var maxByteCount = Encoding.UTF8.GetMaxByteCount(chars.Length);
        var span = _destination.GetSpan(maxByteCount);
        var written = Encoding.UTF8.GetBytes(chars, span);
        _destination.Advance(written);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUtf8(string value) => WriteUtf8(value.AsSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(string value) => WriteUtf8(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(char value) => WriteByte((byte)value);

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
            var span = _destination.GetSpan(written);
            buffer[..written].CopyTo(span);
            _destination.Advance(written);
        }
    }

    public void WriteRepeated(byte value, int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (count <= RepeatedByteStackLimit)
        {
            var span = _destination.GetSpan(count);
            span[..count].Fill(value);
            _destination.Advance(count);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            rented.AsSpan(0, count).Fill(value);
            var span = _destination.GetSpan(count);
            rented.AsSpan(0, count).CopyTo(span);
            _destination.Advance(count);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
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
        var span = _destination.GetSpan(written);
        buffer[..written].CopyTo(span);
        _destination.Advance(written);
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
