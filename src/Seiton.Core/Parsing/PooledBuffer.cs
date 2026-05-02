using System.Buffers;
using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

/// <summary>
/// ArrayPool-backed growable buffer. Encapsulates Rent/Return lifecycle and growth.
/// Use as a drop-in replacement for <c>List&lt;T&gt;</c> in parser hot paths to avoid
/// per-parse heap allocations for internal buffers.
/// </summary>
internal struct PooledBuffer<T>
{
    private T[] _items;
    private int _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBuffer(int initialCapacity)
    {
        _items = ArrayPool<T>.Shared.Rent(initialCapacity);
        _count = 0;
    }

    public readonly int Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Add(T item)
    {
        if (_count == _items.Length)
        {
            Grow();
        }

        _items[_count] = item;
        return _count++;
    }

    public readonly ReadOnlySpan<T> AsSpan() => _items.AsSpan(0, _count);

    /// <summary>
    /// Resets the count to zero without releasing the pooled array.
    /// The buffer can be reused immediately for the next parse.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset() => _count = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Replace(int index, T item)
    {
        _items[index] = item;
    }

    /// <summary>
    /// Copies the buffer contents to a new array. The pooled buffer remains valid.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T[] ToArray() => _count == 0 ? [] : _items.AsSpan(0, _count).ToArray();

    /// <summary>
    /// Transfers ownership of the backing array to the caller.
    /// Returns the (potentially oversized) pooled array and the valid element count.
    /// After this call the buffer is empty and must not be used further.
    /// The caller assumes responsibility for the array's lifecycle.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (T[] Array, int Count) DetachBuffer()
    {
        var array = _items;
        var count = _count;
        _items = null!;
        _count = 0;
        return (array, count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow()
    {
        var old = _items;
        _items = ArrayPool<T>.Shared.Rent(old.Length * 2);
        old.AsSpan(0, _count).CopyTo(_items);
        ArrayPool<T>.Shared.Return(old);
    }

    public void Dispose()
    {
        if (_items is not null)
        {
            ArrayPool<T>.Shared.Return(_items);
            _items = null!;
        }
    }
}
