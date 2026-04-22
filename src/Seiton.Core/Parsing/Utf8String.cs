namespace Seiton.Core.Parsing;

public readonly struct Utf8String : IEquatable<Utf8String>
{
    private readonly ReadOnlyMemory<byte> _memory;

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

    public int Length => _memory.Length;

    public ReadOnlySpan<byte> Span => _memory.Span;

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
