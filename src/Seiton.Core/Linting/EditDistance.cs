using System.Buffers;
using System.Runtime.CompilerServices;

namespace Seiton.Core.Linting;

/// <summary>Shared Levenshtein edit-distance helper (case-insensitive).</summary>
internal static class EditDistance
{
    /// <summary>
    /// Computes the Levenshtein edit distance between two strings using case-insensitive comparison.
    /// Uses Myers bit-parallel algorithm for strings ≤ 64 chars (O(n) with bit ops),
    /// falls back to 1-row DP with pre-computed lowercase for longer strings.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeIgnoreCase(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;

        // Myers64: pattern must be ≤ 64 chars. Use shorter string as pattern.
        if (left.Length <= 64 && right.Length <= 64 && IsAscii(left) && IsAscii(right))
        {
            // Pattern = shorter side for Myers
            if (left.Length <= right.Length)
                return Myers64IgnoreCase(left, right);
            return Myers64IgnoreCase(right, left);
        }

        return OneRowDpIgnoreCase(left, right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAscii(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] > 127)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Computes the Levenshtein edit distance with an early-termination cutoff.
    /// Returns the exact distance if ≤ <paramref name="maxDistance"/>, otherwise returns <paramref name="maxDistance"/> + 1.
    /// Uses length-difference pre-filter and banded DP for efficient cutoff.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeIgnoreCase(string left, string right, int maxDistance)
    {
        if (maxDistance < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDistance), maxDistance, "maxDistance must be non-negative.");

        if (left.Length == 0) return right.Length <= maxDistance ? right.Length : maxDistance + 1;
        if (right.Length == 0) return left.Length <= maxDistance ? left.Length : maxDistance + 1;

        // Length-difference pre-filter: if lengths differ by more than maxDistance, distance must exceed it
        var lengthDiff = Math.Abs(left.Length - right.Length);
        if (lengthDiff > maxDistance)
            return maxDistance + 1;

        // Most suggestion inputs are short ASCII. Reuse the exact Myers fast path,
        // then clamp to maxDistance + 1 when the result exceeds the cutoff.
        if (left.Length <= 64 && right.Length <= 64 && IsAscii(left) && IsAscii(right))
        {
            var exact = left.Length <= right.Length
                ? Myers64IgnoreCase(left, right)
                : Myers64IgnoreCase(right, left);
            return exact <= maxDistance ? exact : maxDistance + 1;
        }

        return BandedDpIgnoreCase(left, right, maxDistance);
    }

    /// <summary>
    /// Myers bit-parallel algorithm for case-insensitive Levenshtein distance.
    /// pattern.Length must be ≤ 64. text can be any length.
    /// Uses stackalloc'd peq table for ASCII characters (128 entries).
    /// </summary>
    private static int Myers64IgnoreCase(string pattern, string text)
    {
        var m = pattern.Length;
        var n = text.Length;

        // Build peq (pattern equipment) table for ASCII chars using stackalloc
        Span<ulong> peq = stackalloc ulong[128];
        peq.Clear();

        for (var i = 0; i < m; i++)
        {
            var c = char.ToLowerInvariant(pattern[i]);
            if (c < 128)
                peq[c] |= 1UL << i;
        }

        // Myers algorithm
        var score = m;
        var vp = ~0UL; // all 1s
        var vn = 0UL;
        var top = 1UL << (m - 1);

        for (var i = 0; i < n; i++)
        {
            var c = char.ToLowerInvariant(text[i]);
            var pm = c < 128 ? peq[c] : 0UL;

            var x = pm | vn;
            var d0 = ((x & vp) + vp) ^ vp | x;
            var hp = vn | ~(d0 | vp);
            var hn = vp & d0;

            if ((hp & top) != 0)
                score++;
            else if ((hn & top) != 0)
                score--;

            var hpShift = (hp << 1) | 1UL;
            vn = hpShift & d0;
            vp = (hn << 1) | ~(hpShift | d0);
        }

        return score;
    }

    /// <summary>
    /// 1-row DP Levenshtein with pre-computed lowercase for right side.
    /// Fallback for strings longer than 64 characters (both sides).
    /// </summary>
    private static int OneRowDpIgnoreCase(string left, string right)
    {
        var n = right.Length;
        var len = n + 1;

        // For strings > 128 chars, rent buffers from ArrayPool.
        if (n > 128)
            return OneRowDpIgnoreCaseHeap(left, right);

        // Pre-compute right-side lowercase
        Span<char> rightLower = stackalloc char[n];
        for (var j = 0; j < n; j++)
            rightLower[j] = char.ToLowerInvariant(right[j]);

        // 1-row DP with prevDiagonal tracking
        Span<int> row = stackalloc int[len];
        for (var j = 0; j < len; j++)
            row[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            var prevDiagonal = row[0];
            row[0] = i;
            var lc = char.ToLowerInvariant(left[i - 1]);

            for (var j = 1; j <= n; j++)
            {
                var old = row[j];
                var substitutionCost = lc == rightLower[j - 1] ? 0 : 1;
                var insertion = row[j - 1] + 1;
                var deletion = old + 1;
                var substitution = prevDiagonal + substitutionCost;

                row[j] = Math.Min(Math.Min(insertion, deletion), substitution);
                prevDiagonal = old;
            }
        }

        return row[n];
    }

    /// <summary>
    /// ArrayPool-backed 1-row DP fallback for very long strings (&gt; 128 chars).
    /// </summary>
    private static int OneRowDpIgnoreCaseHeap(string left, string right)
    {
        var n = right.Length;
        var len = n + 1;

        var rightLowerArray = ArrayPool<char>.Shared.Rent(n);
        var rowArray = ArrayPool<int>.Shared.Rent(len);

        try
        {
            var rightLower = rightLowerArray.AsSpan(0, n);
            for (var j = 0; j < n; j++)
                rightLower[j] = char.ToLowerInvariant(right[j]);

            var row = rowArray.AsSpan(0, len);
            for (var j = 0; j < len; j++)
                row[j] = j;

            for (var i = 1; i <= left.Length; i++)
            {
                var prevDiagonal = row[0];
                row[0] = i;
                var lc = char.ToLowerInvariant(left[i - 1]);

                for (var j = 1; j <= n; j++)
                {
                    var old = row[j];
                    var substitutionCost = lc == rightLower[j - 1] ? 0 : 1;
                    var insertion = row[j - 1] + 1;
                    var deletion = old + 1;
                    var substitution = prevDiagonal + substitutionCost;

                    row[j] = Math.Min(Math.Min(insertion, deletion), substitution);
                    prevDiagonal = old;
                }
            }

            return row[n];
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rowArray);
            ArrayPool<char>.Shared.Return(rightLowerArray);
        }
    }

    /// <summary>
    /// Banded Levenshtein DP with pre-computed lowercase and early termination.
    /// Only computes cells within diagonal band of width 2*maxDistance+1 (Ukkonen-style).
    /// Returns exact distance if ≤ maxDistance, otherwise maxDistance + 1.
    /// </summary>
    private static int BandedDpIgnoreCase(string left, string right, int maxDistance)
    {
        var m = left.Length;
        var n = right.Length;
        var len = n + 1;

        // Fallback for extremely long strings (shouldn't happen in practice)
        if (n > 128 || len > 128)
        {
            return OneRowDpIgnoreCaseWithCutoff(left, right, maxDistance);
        }

        // Pre-compute right-side lowercase
        Span<char> rightLower = stackalloc char[n];
        for (var j = 0; j < n; j++)
            rightLower[j] = char.ToLowerInvariant(right[j]);

        // 1-row banded DP
        Span<int> row = stackalloc int[len];
        for (var j = 0; j < len; j++)
            row[j] = j;

        for (var i = 1; i <= m; i++)
        {
            var prevDiagonal = row[0];
            row[0] = i;
            var lc = char.ToLowerInvariant(left[i - 1]);

            // Band boundaries: only compute j in [max(1, i-maxDistance), min(n, i+maxDistance)]
            var jMin = Math.Max(1, i - maxDistance);
            var jMax = Math.Min(n, i + maxDistance);

            // Initialize left boundary if band doesn't start at 1
            if (jMin > 1)
            {
                prevDiagonal = row[jMin - 1];
                row[jMin - 1] = maxDistance + 1; // sentinel: out-of-band cells are "infinity"
            }

            var rowMin = maxDistance + 1; // track minimum in this row's band

            for (var j = jMin; j <= jMax; j++)
            {
                var old = row[j];
                var substitutionCost = lc == rightLower[j - 1] ? 0 : 1;

                var insertion = row[j - 1] + 1;
                var deletion = old + 1;
                var substitution = prevDiagonal + substitutionCost;

                var val = Math.Min(Math.Min(insertion, deletion), substitution);
                row[j] = val;
                prevDiagonal = old;

                if (val < rowMin) rowMin = val;
            }

            // If all values in the band exceed maxDistance, early terminate
            if (rowMin > maxDistance)
                return maxDistance + 1;

            // Set out-of-band cells on the right to sentinel
            if (jMax < n)
                row[jMax + 1] = maxDistance + 1;
        }

        return row[n] <= maxDistance ? row[n] : maxDistance + 1;
    }

    /// <summary>
    /// Fallback for banded DP when strings exceed stackalloc limit (> 128 chars).
    /// Uses full 1-row DP with early termination check per row.
    /// </summary>
    private static int OneRowDpIgnoreCaseWithCutoff(string left, string right, int maxDistance)
    {
        var n = right.Length;
        var len = n + 1;

        var rightLowerArray = ArrayPool<char>.Shared.Rent(n);
        var rowArray = ArrayPool<int>.Shared.Rent(len);

        try
        {
            var rightLower = rightLowerArray.AsSpan(0, n);
            for (var j = 0; j < n; j++)
                rightLower[j] = char.ToLowerInvariant(right[j]);

            var row = rowArray.AsSpan(0, len);
            for (var j = 0; j < len; j++)
                row[j] = j;

            for (var i = 1; i <= left.Length; i++)
            {
                var prevDiagonal = row[0];
                row[0] = i;
                var lc = char.ToLowerInvariant(left[i - 1]);
                var rowMin = int.MaxValue;

                for (var j = 1; j <= n; j++)
                {
                    var old = row[j];
                    var substitutionCost = lc == rightLower[j - 1] ? 0 : 1;
                    var insertion = row[j - 1] + 1;
                    var deletion = old + 1;
                    var substitution = prevDiagonal + substitutionCost;

                    var val = Math.Min(Math.Min(insertion, deletion), substitution);
                    row[j] = val;
                    prevDiagonal = old;

                    if (val < rowMin) rowMin = val;
                }

                if (rowMin > maxDistance)
                    return maxDistance + 1;
            }

            return row[n] <= maxDistance ? row[n] : maxDistance + 1;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rowArray);
            ArrayPool<char>.Shared.Return(rightLowerArray);
        }
    }
}
