using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

/// <summary>
/// Key table for <see cref="Utf8MappingDispatch.TryMatchFirstOrdered{TTable}"/>. Implement as an empty <c>readonly struct</c>
/// with static members so the JIT can specialize and inline UTF-8 row access.
/// </summary>
internal interface IUtf8OrderedKeyTable
{
    static abstract int KeyCount { get; }

    static abstract ReadOnlySpan<byte> Utf8Key(int ordinal);
}

/// <summary>
/// Shared UTF-8 key matching for YAML mapping traversal. Intended for small, static key tables (parser hot paths).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TryMatchFirstOrdered{TTable}"/> does not allocate. Matching is linear in <see cref="IUtf8OrderedKeyTable.KeyCount"/>
/// (same work as an equivalent chain of <see cref="ReadOnlySpan{T}.SequenceEqual(ReadOnlySpan{T})"/> checks, with first-hit semantics).
/// </para>
/// <para>
/// For performance, call sites should branch on the matched ordinal with a <c>switch</c> (static dispatch) rather than invoking a
/// <see cref="Delegate"/>. Delegates cannot capture <see cref="ReadOnlySpan{T}"/>; a cached delegate still pays an indirect call per key.
/// </para>
/// </remarks>
internal static class Utf8MappingDispatch
{
    /// <summary>
    /// Returns the first ordinal where <paramref name="keyUtf8"/> equals <see cref="IUtf8OrderedKeyTable.Utf8Key"/> for that row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryMatchFirstOrdered<TTable>(ReadOnlySpan<byte> keyUtf8, out int firstMatchIndex)
        where TTable : IUtf8OrderedKeyTable
    {
        for (var i = 0; i < TTable.KeyCount; i++)
        {
            if (keyUtf8.SequenceEqual(TTable.Utf8Key(i)))
            {
                firstMatchIndex = i;
                return true;
            }
        }

        firstMatchIndex = -1;
        return false;
    }
}
