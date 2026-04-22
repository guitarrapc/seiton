using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

/// <summary>
/// Self-contained XXH64 non-cryptographic hash (xxHash specification v0.7.0+).
/// Scalar-only, zero-allocation, no external dependencies.
/// </summary>
internal static class XxHash64
{
    private const ulong Prime1 = 11400714785074694791;
    private const ulong Prime2 = 14029467366897019727;
    private const ulong Prime3 = 1609587929392839161;
    private const ulong Prime4 = 9650029242287828579;
    private const ulong Prime5 = 2870177450012600261;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        var length = data.Length;
        ulong h64;

        if (length >= 32)
        {
            var v1 = seed + Prime1 + Prime2;
            var v2 = seed + Prime2;
            var v3 = seed;
            var v4 = seed - Prime1;

            var offset = 0;
            do
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset)));
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 8)));
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 16)));
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 24)));
                offset += 32;
            }
            while (offset + 32 <= length);

            h64 = RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18);
            h64 = MergeRound(h64, v1);
            h64 = MergeRound(h64, v2);
            h64 = MergeRound(h64, v3);
            h64 = MergeRound(h64, v4);

            // Process remaining bytes after the last 32-byte block
            data = data.Slice(offset);
        }
        else
        {
            h64 = seed + Prime5;
        }

        h64 += (ulong)length;

        // Remaining 8-byte chunks
        while (data.Length >= 8)
        {
            h64 ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data));
            h64 = RotateLeft(h64, 27) * Prime1 + Prime4;
            data = data.Slice(8);
        }

        // Remaining 4-byte chunk
        if (data.Length >= 4)
        {
            h64 ^= BinaryPrimitives.ReadUInt32LittleEndian(data) * Prime1;
            h64 = RotateLeft(h64, 23) * Prime2 + Prime3;
            data = data.Slice(4);
        }

        // Remaining bytes
        for (var i = 0; i < data.Length; i++)
        {
            h64 ^= data[i] * Prime5;
            h64 = RotateLeft(h64, 11) * Prime1;
        }

        // Final avalanche
        h64 ^= h64 >> 33;
        h64 *= Prime2;
        h64 ^= h64 >> 29;
        h64 *= Prime3;
        h64 ^= h64 >> 32;

        return h64;
    }

    /// <summary>
    /// Returns the lower 32 bits of the XXH64 hash, suitable for GetHashCode().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Hash32(ReadOnlySpan<byte> data, ulong seed = 0)
        => unchecked((int)Hash(data, seed));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Round(ulong acc, ulong input)
    {
        acc += input * Prime2;
        acc = RotateLeft(acc, 31);
        acc *= Prime1;
        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MergeRound(ulong acc, ulong val)
    {
        val = Round(0, val);
        acc ^= val;
        acc = acc * Prime1 + Prime4;
        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong RotateLeft(ulong value, int count)
        => (value << count) | (value >> (64 - count));
}
