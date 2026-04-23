namespace Seiton.Core.Parsing;

/// <summary>Zero-copy reference to a byte range in the YAML source, represented as offset+length.</summary>
public readonly record struct Utf8Slice(int Offset, int Length)
{
    /// <summary>Gets whether this slice represents an empty or missing value.</summary>
    public bool IsEmpty => Length <= 0;

    /// <summary>Copies the referenced bytes into a new <see cref="Utf8String"/>.</summary>
    public Utf8String ToUtf8String(ReadOnlySpan<byte> source)
    {
        return new Utf8String(AsSpan(source));
    }

    internal Utf8String ToUtf8StringZeroCopy(byte[] source)
    {
        if (Offset < 0 || Length < 0 || Offset + Length > source.Length)
        {
            return default;
        }

        return new Utf8String(source.AsMemory(Offset, Length));
    }

    /// <summary>Returns the referenced bytes as a span into <paramref name="source"/>.</summary>
    public ReadOnlySpan<byte> AsSpan(ReadOnlySpan<byte> source)
    {
        if (Offset < 0 || Length < 0 || Offset + Length > source.Length)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        return source.Slice(Offset, Length);
    }
}
