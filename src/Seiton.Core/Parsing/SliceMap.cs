using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

/// <summary>
/// Flat map with Utf8Slice keys resolved against a source byte span.
/// Replaces <c>Dictionary&lt;Utf8String, TValue&gt;</c> in AST nodes to eliminate per-key byte[] allocation.
/// Keys are stored as zero-copy <see cref="Utf8Slice"/> into the original YAML source bytes.
/// Lookup is linear scan (optimal for typical GitHub Actions map sizes: 1–25 entries).
/// </summary>
public readonly struct SliceMap<TValue>
{
    /// <summary>A key-value pair stored in the map.</summary>
    public readonly struct Entry(Utf8Slice key, TValue value)
    {
        public readonly Utf8Slice Key = key;
        public readonly TValue Value = value;

        /// <summary>Deconstructs into key and value.</summary>
        public void Deconstruct(out Utf8Slice key, out TValue value)
        {
            key = Key;
            value = Value;
        }
    }

    private readonly Entry[]? _entries;
    private readonly int _count;
    private readonly bool _caseSensitive;

    public SliceMap(Entry[] entries, bool caseSensitive)
    {
        _entries = entries;
        _count = entries.Length;
        _caseSensitive = caseSensitive;
    }

    /// <summary>
    /// Constructs a SliceMap from a potentially oversized array with an explicit element count.
    /// </summary>
    /// <param name="entries">The backing array. Its length must be &gt;= <paramref name="count"/>.</param>
    /// <param name="count">The number of valid elements in <paramref name="entries"/>. Must be between 0 and <c>entries.Length</c>.</param>
    /// <param name="caseSensitive">Whether key lookup is case-sensitive.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="count"/> is negative or greater than <paramref name="entries"/>.Length.
    /// </exception>
    public SliceMap(Entry[] entries, int count, bool caseSensitive)
    {
        if ((uint)count > (uint)entries.Length)
            throw new ArgumentOutOfRangeException(nameof(count), count, $"count must be between 0 and entries.Length ({entries.Length}).");
        _entries = entries;
        _count = count;
        _caseSensitive = caseSensitive;
    }

    /// <summary>Gets the number of entries in the map.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    /// <summary>Looks up a value by raw UTF-8 key bytes against the YAML <paramref name="source"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key, out TValue value)
    {
        if (_entries is not null)
        {
            var len = _count;
            for (var i = 0; i < len; i++)
            {
                if (KeyEquals(source, _entries[i].Key, key))
                {
                    value = _entries[i].Value;
                    return true;
                }
            }
        }

        value = default!;
        return false;
    }

    /// <summary>Returns whether the map contains the given key.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key)
        => TryGetValue(source, key, out _);

    /// <summary>
    /// Overload accepting <see cref="Utf8Slice"/> key, resolved against <paramref name="source"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(ReadOnlySpan<byte> source, Utf8Slice key)
        => ContainsKey(source, key.AsSpan(source));

    /// <summary>Looks up the index of the entry with the given key.</summary>
    public bool TryGetIndex(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key, out int index)
    {
        if (_entries is not null)
        {
            var len = _count;
            for (var i = 0; i < len; i++)
            {
                if (KeyEquals(source, _entries[i].Key, key))
                {
                    index = i;
                    return true;
                }
            }
        }

        index = -1;
        return false;
    }

    /// <summary>Returns the entries as a span for iteration.</summary>
    public ReadOnlySpan<Entry> Entries => _entries is null ? [] : _entries.AsSpan(0, _count);

    /// <summary>Returns an enumerator over all entries.</summary>
    public Enumerator GetEnumerator() => new(_entries, _count);

    /// <summary>Forward-only enumerator over <see cref="SliceMap{TValue}"/> entries.</summary>
    public struct Enumerator
    {
        private readonly Entry[]? _entries;
        private readonly int _count;
        private int _index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(Entry[]? entries, int count)
        {
            _entries = entries;
            _count = count;
            _index = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => ++_index < _count;

        public readonly ref readonly Entry Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _entries![_index];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool KeyEquals(ReadOnlySpan<byte> source, Utf8Slice stored, ReadOnlySpan<byte> needle)
    {
        var storedSpan = stored.AsSpan(source);
        return _caseSensitive
            ? storedSpan.SequenceEqual(needle)
            : AsciiEqualsIgnoreCase(storedSpan, needle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            var ca = a[i];
            var cb = b[i];
            if (ca == cb) continue;
            if (ca is >= (byte)'A' and <= (byte)'Z') ca = (byte)(ca + 32);
            if (cb is >= (byte)'A' and <= (byte)'Z') cb = (byte)(cb + 32);
            if (ca != cb) return false;
        }

        return true;
    }
}
