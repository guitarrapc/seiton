using System.Collections;
using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

/// <summary>
/// A diagnostic collection that may be backed by a pooled array larger than the valid count.
/// Implements <see cref="IReadOnlyList{T}"/> for LINQ compatibility while providing
/// <see cref="AsSpan"/> for zero-copy hot-path access.
/// </summary>
public readonly struct DiagnosticList : IReadOnlyList<Diagnostic>
{
    private readonly Diagnostic[]? _array;
    private readonly int _count;

    /// <summary>Creates a list from a pre-allocated array. All elements are valid.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DiagnosticList(Diagnostic[] array)
    {
        _array = array;
        _count = array.Length;
    }

    /// <summary>Creates a list from a pooled (potentially oversized) array with an explicit count.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DiagnosticList(Diagnostic[] array, int count)
    {
        _array = array;
        _count = count;
    }

    /// <summary>Number of valid diagnostics.</summary>
    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    /// <inheritdoc/>
    int IReadOnlyCollection<Diagnostic>.Count => _count;

    /// <summary>Gets the diagnostic at the specified index.</summary>
    public Diagnostic this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _array![index];
    }

    /// <summary>Returns the valid diagnostics as a span (zero-copy).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Diagnostic> AsSpan() =>
        _array is null ? [] : _array.AsSpan(0, _count);

    /// <summary>Implicit conversion from <see cref="Diagnostic"/>[] for backward compatibility.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator DiagnosticList(Diagnostic[] array) => new(array);

    /// <summary>Gets a struct enumerator for zero-alloc foreach.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator() => new(_array, _count);

    IEnumerator<Diagnostic> IEnumerable<Diagnostic>.GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
            yield return _array![i];
    }

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Diagnostic>)this).GetEnumerator();

    /// <summary>Zero-alloc enumerator for foreach loops.</summary>
    public struct Enumerator
    {
        private readonly Diagnostic[]? _array;
        private readonly int _count;
        private int _index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(Diagnostic[]? array, int count)
        {
            _array = array;
            _count = count;
            _index = -1;
        }

        public readonly Diagnostic Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _array![_index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => ++_index < _count;
    }
}
