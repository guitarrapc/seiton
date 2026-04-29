namespace Seiton.Core.Linting;

/// <summary>Shared Levenshtein edit-distance helper (case-insensitive).</summary>
internal static class EditDistance
{
    /// <summary>
    /// Computes the Levenshtein edit distance between two strings using case-insensitive comparison.
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

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
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
}
