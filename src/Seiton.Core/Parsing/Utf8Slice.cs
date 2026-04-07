namespace Seiton.Core.Parsing;

public readonly record struct Utf8Slice(int Offset, int Length)
{
    public bool IsEmpty => Length <= 0;

    public ReadOnlySpan<byte> AsSpan(ReadOnlySpan<byte> source)
    {
        if (Offset < 0 || Length < 0 || Offset + Length > source.Length)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        return source.Slice(Offset, Length);
    }
}
