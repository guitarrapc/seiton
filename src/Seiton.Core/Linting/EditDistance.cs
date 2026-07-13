using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Seiton.Core.Linting;

/// <summary>Shared Levenshtein edit-distance helper (case-insensitive).</summary>
internal static class EditDistance
{
    /// <summary>
    /// Computes the Levenshtein edit distance between two strings using case-insensitive comparison.
    /// Myers bit-parallel constrains only the pattern (shorter) side: 1-word Myers when the
    /// shorter side is ≤ 64 chars, 2-word Myers when it is 65–128 chars (text side unbounded),
    /// falling back to 1-row DP with pre-computed lowercase beyond that or for non-ASCII input.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeIgnoreCase(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;

        // Fast path: both short ASCII (the common suggestion case).
        if (left.Length <= 64 && right.Length <= 64 && IsAscii(left) && IsAscii(right))
        {
            // Pattern = shorter side for Myers
            if (left.Length <= right.Length)
                return Myers64IgnoreCase(left, right);
            return Myers64IgnoreCase(right, left);
        }

        return ComputeIgnoreCaseExtended(left, right);
    }

    /// <summary>
    /// Non-inlined tail of the unbounded dispatch: keeps the aggressively-inlined fast path
    /// small so hot caller loops retain their pre-existing code footprint.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ComputeIgnoreCaseExtended(string left, string right)
    {
        if (IsAscii(left) && IsAscii(right))
        {
            var (pattern, text) = left.Length <= right.Length ? (left, right) : (right, left);
            if (pattern.Length <= 64)
                return Myers64IgnoreCase(pattern, text);
            if (pattern.Length <= 128)
                return Myers128IgnoreCase(pattern, text);
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

        return ComputeIgnoreCaseExtended(left, right, maxDistance);
    }

    /// <summary>
    /// Non-inlined tail of the bounded dispatch: keeps the aggressively-inlined fast path
    /// small so hot caller loops retain their pre-existing code footprint.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ComputeIgnoreCaseExtended(string left, string right, int maxDistance)
    {
        if (IsAscii(left) && IsAscii(right))
        {
            var (pattern, text) = left.Length <= right.Length ? (left, right) : (right, left);
            if (pattern.Length <= 64)
            {
                var exact = Myers64IgnoreCase(pattern, text);
                return exact <= maxDistance ? exact : maxDistance + 1;
            }

            if (pattern.Length <= 128)
            {
                var exact = Myers128IgnoreCase(pattern, text);
                return exact <= maxDistance ? exact : maxDistance + 1;
            }
        }

        return BandedDpIgnoreCase(left, right, maxDistance);
    }

    /// <summary>
    /// Computes <see cref="ComputeIgnoreCase(string, string, int)"/> for one input against many
    /// candidates, writing each clamped distance to <paramref name="distances"/>. Semantically
    /// identical to calling the per-pair overload per candidate, but builds the Myers peq table
    /// from the input once and, when AVX2 is available, computes four candidates per iteration
    /// (one per 64-bit lane of a <see cref="Vector256{T}"/>).
    /// </summary>
    public static void ComputeIgnoreCaseMany(string input, ReadOnlySpan<string> candidates, int maxDistance, Span<int> distances)
    {
        if (maxDistance < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDistance), maxDistance, "maxDistance must be non-negative.");
        if (distances.Length < candidates.Length)
            throw new ArgumentException("distances must be at least as long as candidates.", nameof(distances));

        // Shared-peq path requires an ASCII input usable as the Myers pattern.
        if (input.Length == 0 || input.Length > 64 || !IsAscii(input))
        {
            for (var i = 0; i < candidates.Length; i++)
                distances[i] = ComputeIgnoreCase(input, candidates[i], maxDistance);
            return;
        }

        var m = input.Length;
        Span<ulong> peq = stackalloc ulong[128];
        peq.Clear();

        for (var i = 0; i < m; i++)
        {
            var c = char.ToLowerInvariant(input[i]);
            if (c < 128)
                peq[c] |= 1UL << i;
        }

        if (Avx2.IsSupported)
        {
            ComputeManyAvx2(peq, m, input, candidates, maxDistance, distances);
            return;
        }

        for (var i = 0; i < candidates.Length; i++)
            distances[i] = ComputeOneSharedPeq(peq, m, input, candidates[i], maxDistance);
    }

    /// <summary>One candidate against the prebuilt input peq; falls back per-pair when the candidate cannot ride the shared pattern.</summary>
    private static int ComputeOneSharedPeq(ReadOnlySpan<ulong> peq, int m, string input, string candidate, int maxDistance)
    {
        if (Math.Abs(m - candidate.Length) > maxDistance)
            return maxDistance + 1;

        if (candidate.Length == 0)
            return m <= maxDistance ? m : maxDistance + 1;

        if (candidate.Length <= 64 && IsAscii(candidate))
        {
            var exact = Myers64SharedPeq(peq, m, candidate);
            return exact <= maxDistance ? exact : maxDistance + 1;
        }

        return ComputeIgnoreCase(input, candidate, maxDistance);
    }

    /// <summary>AVX2 batch: eligible candidates ride four per Vector256; the ragged tail and fallbacks stay scalar.</summary>
    private static void ComputeManyAvx2(ReadOnlySpan<ulong> peq, int m, string input, ReadOnlySpan<string> candidates, int maxDistance, Span<int> distances)
    {
        Span<int> laneIdx = stackalloc int[4];
        Span<int> laneScores = stackalloc int[4];
        var laneCount = 0;

        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            if (Math.Abs(m - candidate.Length) > maxDistance)
            {
                distances[i] = maxDistance + 1;
            }
            else if (candidate.Length == 0)
            {
                distances[i] = m <= maxDistance ? m : maxDistance + 1;
            }
            else if (candidate.Length <= 64 && IsAscii(candidate))
            {
                laneIdx[laneCount++] = i;
                if (laneCount == 4)
                {
                    Myers64FourLanes(
                        peq, m,
                        candidates[laneIdx[0]], candidates[laneIdx[1]],
                        candidates[laneIdx[2]], candidates[laneIdx[3]],
                        laneScores);
                    for (var l = 0; l < 4; l++)
                    {
                        var exact = laneScores[l];
                        distances[laneIdx[l]] = exact <= maxDistance ? exact : maxDistance + 1;
                    }

                    laneCount = 0;
                }
            }
            else
            {
                distances[i] = ComputeIgnoreCase(input, candidate, maxDistance);
            }
        }

        for (var l = 0; l < laneCount; l++)
        {
            var exact = Myers64SharedPeq(peq, m, candidates[laneIdx[l]]);
            distances[laneIdx[l]] = exact <= maxDistance ? exact : maxDistance + 1;
        }
    }

    /// <summary>Myers text loop over a prebuilt peq (pattern = input, m ≤ 64).</summary>
    private static int Myers64SharedPeq(ReadOnlySpan<ulong> peq, int m, string text)
    {
        var n = text.Length;
        var score = m;
        var vp = ~0UL;
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
    /// 4-lane Myers: one ASCII candidate (≤ 64 chars) per ulong lane, shared peq built from the
    /// input. Each lane's exact score is captured at its own text length; finished lanes keep
    /// streaming pm = 0 harmlessly until the longest lane completes.
    /// </summary>
    private static void Myers64FourLanes(ReadOnlySpan<ulong> peq, int m, string t0, string t1, string t2, string t3, Span<int> scores)
    {
        var n0 = t0.Length;
        var n1 = t1.Length;
        var n2 = t2.Length;
        var n3 = t3.Length;
        var nMax = Math.Max(Math.Max(n0, n1), Math.Max(n2, n3));

        var vp = Vector256.Create(~0UL);
        var vn = Vector256<ulong>.Zero;
        var top = Vector256.Create(1UL << (m - 1));
        var one = Vector256.Create(1UL);
        var score = Vector256.Create((ulong)m);
        var shift = m - 1;

        var s0 = m;
        var s1 = m;
        var s2 = m;
        var s3 = m;

        for (var i = 0; i < nMax; i++)
        {
            var pm = Vector256.Create(
                i < n0 ? peq[char.ToLowerInvariant(t0[i])] : 0UL,
                i < n1 ? peq[char.ToLowerInvariant(t1[i])] : 0UL,
                i < n2 ? peq[char.ToLowerInvariant(t2[i])] : 0UL,
                i < n3 ? peq[char.ToLowerInvariant(t3[i])] : 0UL);

            var x = pm | vn;
            var d0 = (((x & vp) + vp) ^ vp) | x;
            var hp = vn | ~(d0 | vp);
            var hn = vp & d0;

            score += Vector256.ShiftRightLogical(hp & top, shift);
            score -= Vector256.ShiftRightLogical(hn & top, shift);

            var hpShift = Vector256.ShiftLeft(hp, 1) | one;
            vn = hpShift & d0;
            vp = Vector256.ShiftLeft(hn, 1) | ~(hpShift | d0);

            var col = i + 1;
            if (col == n0) s0 = (int)score.GetElement(0);
            if (col == n1) s1 = (int)score.GetElement(1);
            if (col == n2) s2 = (int)score.GetElement(2);
            if (col == n3) s3 = (int)score.GetElement(3);
        }

        scores[0] = s0;
        scores[1] = s1;
        scores[2] = s2;
        scores[3] = s3;
    }

    /// <summary>
    /// Hardcoded 2-word Myers for 64 &lt; pattern.Length ≤ 128 (text unbounded). The 128-bit
    /// VP/VN state lives in two ulongs; the D0 addition chains a carry from word 0 to word 1
    /// and the HP/HN shifts carry the top bit across the word boundary.
    /// </summary>
    private static int Myers128IgnoreCase(string pattern, string text)
    {
        var m = pattern.Length;
        var n = text.Length;

        Span<ulong> peq0 = stackalloc ulong[128];
        Span<ulong> peq1 = stackalloc ulong[128];
        peq0.Clear();
        peq1.Clear();

        for (var i = 0; i < 64; i++)
        {
            var c = char.ToLowerInvariant(pattern[i]);
            if (c < 128)
                peq0[c] |= 1UL << i;
        }

        for (var i = 64; i < m; i++)
        {
            var c = char.ToLowerInvariant(pattern[i]);
            if (c < 128)
                peq1[c] |= 1UL << (i - 64);
        }

        var score = m;
        var vp0 = ~0UL;
        var vp1 = ~0UL;
        var vn0 = 0UL;
        var vn1 = 0UL;
        var top = 1UL << (m - 65);

        for (var i = 0; i < n; i++)
        {
            var c = char.ToLowerInvariant(text[i]);
            var pm0 = 0UL;
            var pm1 = 0UL;
            if (c < 128)
            {
                pm0 = peq0[c];
                pm1 = peq1[c];
            }

            var x0 = pm0 | vn0;
            var a0 = x0 & vp0;
            var sum0 = a0 + vp0;
            var carry = sum0 < a0 ? 1UL : 0UL;
            var d00 = (sum0 ^ vp0) | x0;

            var x1 = pm1 | vn1;
            var a1 = x1 & vp1;
            var sum1 = a1 + vp1 + carry;
            var d01 = (sum1 ^ vp1) | x1;

            var hp0 = vn0 | ~(d00 | vp0);
            var hn0 = vp0 & d00;
            var hp1 = vn1 | ~(d01 | vp1);
            var hn1 = vp1 & d01;

            if ((hp1 & top) != 0)
                score++;
            else if ((hn1 & top) != 0)
                score--;

            var hpShift0 = (hp0 << 1) | 1UL;
            var hpShift1 = (hp1 << 1) | (hp0 >> 63);
            var hnShift0 = hn0 << 1;
            var hnShift1 = (hn1 << 1) | (hn0 >> 63);

            vn0 = hpShift0 & d00;
            vp0 = hnShift0 | ~(hpShift0 | d00);
            vn1 = hpShift1 & d01;
            vp1 = hnShift1 | ~(hpShift1 | d01);
        }

        return score;
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
