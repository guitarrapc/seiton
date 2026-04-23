namespace Seiton.Core.Parsing;

/// <summary>Immutable owned copy of a UTF-8 byte sequence, used as dictionary key in expression types.</summary>
public readonly struct Utf8String : IEquatable<Utf8String>
{
    private readonly ReadOnlyMemory<byte> _memory;

    /// <summary>Creates a new <see cref="Utf8String"/> by copying the given UTF-8 bytes.</summary>
    public Utf8String(ReadOnlySpan<byte> utf8)
    {
        _memory = utf8.ToArray();
    }

    internal Utf8String(ReadOnlyMemory<byte> memory)
    {
        _memory = memory;
    }

    private Utf8String(byte[] owned)
    {
        _memory = new ReadOnlyMemory<byte>(owned);
    }

    /// <summary>Gets the byte length of the UTF-8 sequence.</summary>
    public int Length => _memory.Length;

    /// <summary>Gets the raw UTF-8 bytes as a span.</summary>
    public ReadOnlySpan<byte> Span => _memory.Span;

    /// <summary>Creates a lowercased ASCII copy of the given UTF-8 bytes.</summary>
    public static Utf8String FromLowerAscii(ReadOnlySpan<byte> utf8)
    {
        var bytes = utf8.ToArray();
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b is >= (byte)'A' and <= (byte)'Z')
            {
                bytes[i] = (byte)(b + 32);
            }
        }

        return new Utf8String(bytes);
    }

    public bool Equals(Utf8String other) => Span.SequenceEqual(other.Span);

    public override bool Equals(object? obj) => obj is Utf8String other && Equals(other);

    public override int GetHashCode()
    {
        return XxHash64.Hash32(Span);
    }

    public static bool operator ==(Utf8String left, Utf8String right) => left.Equals(right);

    public static bool operator !=(Utf8String left, Utf8String right) => !left.Equals(right);
}
