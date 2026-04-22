using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Seiton.Core.Parsing;

/// <summary>
/// Self-contained XXH64 non-cryptographic hash (xxHash specification v0.7.0+).
/// Scalar-only, zero-allocation, no external dependencies.
/// Uses ref-based pointer arithmetic to avoid Span.Slice bounds checks in hot loops.
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
        ref byte r0 = ref MemoryMarshal.GetReference(data);
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
                v1 = Round(v1, ReadUInt64(ref r0, offset));
                v2 = Round(v2, ReadUInt64(ref r0, offset + 8));
                v3 = Round(v3, ReadUInt64(ref r0, offset + 16));
                v4 = Round(v4, ReadUInt64(ref r0, offset + 24));
                offset += 32;
            }
            while (offset + 32 <= length);

            h64 = RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18);
            h64 = MergeRound(h64, v1);
            h64 = MergeRound(h64, v2);
            h64 = MergeRound(h64, v3);
            h64 = MergeRound(h64, v4);

            // Advance past processed 32-byte blocks
            r0 = ref Unsafe.Add(ref r0, offset);
            length -= offset;
        }
        else
        {
            h64 = seed + Prime5;
        }

        h64 += (ulong)data.Length;

        // Remaining 8-byte chunks
        var rem = 0;
        while (rem + 8 <= length)
        {
            h64 ^= Round(0, ReadUInt64(ref r0, rem));
            h64 = RotateLeft(h64, 27) * Prime1 + Prime4;
            rem += 8;
        }

        // Remaining 4-byte chunk
        if (rem + 4 <= length)
        {
            h64 ^= ReadUInt32(ref r0, rem) * Prime1;
            h64 = RotateLeft(h64, 23) * Prime2 + Prime3;
            rem += 4;
        }

        // Remaining bytes
        while (rem < length)
        {
            h64 ^= Unsafe.Add(ref r0, rem) * Prime5;
            h64 = RotateLeft(h64, 11) * Prime1;
            rem++;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUInt64(ref byte origin, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref origin, offset), 8));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUInt32(ref byte origin, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref origin, offset), 4));
}
