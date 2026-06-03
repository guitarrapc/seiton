using System.Collections;
using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

/// <summary>
/// A read-only list backed by an array (typically pooled and registered with <see cref="AstArena"/>).
/// Avoids per-parse <c>ToArray()</c> copies while keeping <see cref="IReadOnlyList{T}"/> compatibility.
/// </summary>
internal readonly struct ArenaList<T> : IReadOnlyList<T>
{
    private readonly T[]? _array;
    private readonly int _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ArenaList(T[] array, int count)
    {
        _array = array;
        _count = count;
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    int IReadOnlyCollection<T>.Count => _count;

    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _array![index];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> AsSpan() => _array is null ? [] : _array.AsSpan(0, _count);

    public Enumerator GetEnumerator() => new(_array, _count);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator(T[]? array, int count) : IEnumerator<T>
    {
        private readonly T[]? _array = array;
        private readonly int _count = count;
        private int _index = -1;

        public T Current => _array![_index];

        object IEnumerator.Current => Current!;

        public bool MoveNext() => ++_index < _count;

        public void Reset() => _index = -1;

        public void Dispose() { }
    }
}
