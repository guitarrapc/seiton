using System.Buffers;
using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

/// <summary>
/// Append-only row table for data-oriented AST nodes, backed by <see cref="ArrayPool{T}"/>.
/// Rows are addressed by index (wrapped in typed 1-based node IDs by <see cref="AstArena"/>).
/// Reset keeps capacity for reuse across parses; oversized backing arrays are released on
/// arena disposal via <see cref="ReleaseOversized"/>.
/// Invariant: releasing the backing array always clears the count as well, so a table can
/// never address rows beyond its (possibly re-rented) backing array.
/// </summary>
internal struct NodeTable<T> where T : struct
{
    private const int DefaultCapacity = 8;

    private T[]? _rows;
    private int _count;

    public readonly int Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Add(in T row)
    {
        if (_rows is null)
        {
            _rows = ArrayPool<T>.Shared.Rent(Math.Max(DefaultCapacity, _count + 1));
        }
        else if (_count == _rows.Length)
        {
            var grown = ArrayPool<T>.Shared.Rent(_rows.Length * 2);
            Array.Copy(_rows, grown, _count);
            ArrayPool<T>.Shared.Return(_rows);
            _rows = grown;
        }

        _rows[_count] = row;
        return _count++;
    }

    public readonly ref readonly T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _rows![index];
    }

    /// <summary>Clears the row count while retaining capacity for the next parse.</summary>
    public void Reset() => _count = 0;

    /// <summary>Copies the first <paramref name="limit"/> rows from <paramref name="source"/> (incremental parse import).</summary>
    public void CopyFrom(in NodeTable<T> source, int limit)
    {
        var count = Math.Min(source._count, limit);
        if (count <= 0)
        {
            // Zero the count even for an empty source so stale destination rows can
            // never remain addressable after the copy.
            _count = 0;
            return;
        }

        if (_rows is null || _rows.Length < count)
        {
            if (_rows is not null)
            {
                ArrayPool<T>.Shared.Return(_rows);
            }

            _rows = ArrayPool<T>.Shared.Rent(Math.Max(count, DefaultCapacity));
        }

        Array.Copy(source._rows!, _rows, count);
        _count = count;
    }

    /// <summary>Returns the backing array to the pool when it grew beyond <paramref name="maxRetainedCapacity"/>.</summary>
    public void ReleaseOversized(int maxRetainedCapacity)
    {
        if (_rows is not null && _rows.Length > maxRetainedCapacity)
        {
            ArrayPool<T>.Shared.Return(_rows);
            _rows = null;
            _count = 0;
        }
    }

    /// <summary>Returns the backing array to the pool unconditionally (arena discard path).</summary>
    public void ReleaseAll()
    {
        if (_rows is not null)
        {
            ArrayPool<T>.Shared.Return(_rows);
            _rows = null;
        }

        _count = 0;
    }
}
