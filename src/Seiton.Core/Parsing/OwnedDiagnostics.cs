using System.Collections;
using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

/// <summary>
/// A caller-owned diagnostic collection that is safe to retain indefinitely.
/// <para>
/// Unlike <see cref="DiagnosticList"/> (which may reference arena-pooled memory),
/// <see cref="OwnedDiagnostics"/> always owns its backing array and is not tied to
/// any arena or result lifetime. Use <see cref="ParseResult.CopyDiagnostics"/> or
/// <see cref="Linting.LintResult.CopyDiagnostics"/> to obtain an instance.
/// </para>
/// </summary>
[CollectionBuilder(typeof(OwnedDiagnostics), nameof(Create))]
public readonly struct OwnedDiagnostics : IReadOnlyList<Diagnostic>
{
    private readonly Diagnostic[]? _array;

    /// <summary>Creates an OwnedDiagnostics from a span (used by collection expressions).</summary>
    public static OwnedDiagnostics Create(ReadOnlySpan<Diagnostic> items) =>
        items.Length == 0 ? default : new OwnedDiagnostics(items.ToArray());

    /// <summary>Creates an owned diagnostics collection from a caller-owned array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public OwnedDiagnostics(Diagnostic[] array)
    {
        _array = array;
    }

    /// <summary>Number of diagnostics.</summary>
    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _array?.Length ?? 0;
    }

    /// <inheritdoc/>
    int IReadOnlyCollection<Diagnostic>.Count => Length;

    /// <summary>Gets the diagnostic at the specified index.</summary>
    public Diagnostic this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_array is null || (uint)index >= (uint)_array.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _array[index];
        }
    }

    /// <summary>Returns the diagnostics as a span (zero-copy).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Diagnostic> AsSpan() =>
        _array ?? [];

    /// <summary>Returns the underlying owned array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Diagnostic[] AsArray() => _array ?? [];

    /// <summary>
    /// Implicit conversion to <see cref="Diagnostic"/>[] for backward compatibility
    /// with existing code that expects a raw array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Diagnostic[](OwnedDiagnostics owned) => owned.AsArray();

    /// <summary>
    /// Implicit conversion to <see cref="ReadOnlySpan{T}"/> for span-based consumption.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ReadOnlySpan<Diagnostic>(OwnedDiagnostics owned) => owned.AsSpan();

    /// <summary>Gets a struct enumerator for zero-alloc foreach.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator() => new(_array);

    IEnumerator<Diagnostic> IEnumerable<Diagnostic>.GetEnumerator()
    {
        var arr = _array;
        if (arr is null) yield break;
        for (var i = 0; i < arr.Length; i++)
            yield return arr[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Diagnostic>)this).GetEnumerator();

    /// <summary>Zero-alloc enumerator for foreach loops.</summary>
    public struct Enumerator
    {
        private readonly Diagnostic[]? _array;
        private int _index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(Diagnostic[]? array)
        {
            _array = array;
            _index = -1;
        }

        public readonly Diagnostic Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _array![_index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _array is not null && ++_index < _array.Length;
    }
}
