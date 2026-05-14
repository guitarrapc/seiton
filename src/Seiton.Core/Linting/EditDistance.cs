using System.Buffers;

namespace Seiton.Core.Linting;

/// <summary>Shared Levenshtein edit-distance helper (case-insensitive).</summary>
internal static class EditDistance
{
    /// <summary>
    /// Computes the Levenshtein edit distance between two strings using case-insensitive comparison.
    /// Uses stackalloc for strings up to 128 chars to avoid heap allocations in loop scenarios.
    /// </summary>
    public static int ComputeIgnoreCase(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var len = right.Length + 1;
        if (len <= 128)
        {
            Span<int> previous = stackalloc int[len];
            Span<int> current = stackalloc int[len];

            for (var j = 0; j < len; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                var lc = char.ToLowerInvariant(left[i - 1]);
                for (var j = 1; j <= right.Length; j++)
                {
                    var rc = char.ToLowerInvariant(right[j - 1]);
                    var substitutionCost = lc == rc ? 0 : 1;
                    var deletion = previous[j] + 1;
                    var insertion = current[j - 1] + 1;
                    var substitution = previous[j - 1] + substitutionCost;

                    current[j] = Math.Min(Math.Min(deletion, insertion), substitution);
                }

                var tmp = previous;
                previous = current;
                current = tmp;
            }

            return previous[right.Length];
        }
        else
        {
            var rentedPrev = ArrayPool<int>.Shared.Rent(len);
            var rentedCurr = ArrayPool<int>.Shared.Rent(len);
            try
            {
                var previous = rentedPrev.AsSpan(0, len);
                var current = rentedCurr.AsSpan(0, len);

                for (var j = 0; j < len; j++)
                {
                    previous[j] = j;
                }

                for (var i = 1; i <= left.Length; i++)
                {
                    current[0] = i;
                    var lc = char.ToLowerInvariant(left[i - 1]);
                    for (var j = 1; j <= right.Length; j++)
                    {
                        var rc = char.ToLowerInvariant(right[j - 1]);
                        var substitutionCost = lc == rc ? 0 : 1;
                        var deletion = previous[j] + 1;
                        var insertion = current[j - 1] + 1;
                        var substitution = previous[j - 1] + substitutionCost;

                        current[j] = Math.Min(Math.Min(deletion, insertion), substitution);
                    }

                    var tmp = previous;
                    previous = current;
                    current = tmp;
                }

                return previous[right.Length];
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rentedPrev);
                ArrayPool<int>.Shared.Return(rentedCurr);
            }
        }
    }
}
