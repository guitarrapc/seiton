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
    public readonly struct Entry(Utf8Slice key, TValue value)
    {
        public readonly Utf8Slice Key = key;
        public readonly TValue Value = value;

        public void Deconstruct(out Utf8Slice key, out TValue value)
        {
            key = Key;
            value = Value;
        }
    }

    private readonly Entry[]? _entries;
    private readonly bool _caseSensitive;

    public SliceMap(Entry[] entries, bool caseSensitive)
    {
        _entries = entries;
        _caseSensitive = caseSensitive;
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _entries?.Length ?? 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key, out TValue value)
    {
        if (_entries is not null)
        {
            for (var i = 0; i < _entries.Length; i++)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key)
        => TryGetValue(source, key, out _);

    /// <summary>
    /// Overload accepting <see cref="Utf8Slice"/> key, resolved against <paramref name="source"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(ReadOnlySpan<byte> source, Utf8Slice key)
        => ContainsKey(source, key.AsSpan(source));

    public bool TryGetIndex(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key, out int index)
    {
        if (_entries is not null)
        {
            for (var i = 0; i < _entries.Length; i++)
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

    public ReadOnlySpan<Entry> Entries => _entries ?? [];

    public Enumerator GetEnumerator() => new(_entries);

    public struct Enumerator
    {
        private readonly Entry[]? _entries;
        private int _index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(Entry[]? entries)
        {
            _entries = entries;
            _index = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => ++_index < (_entries?.Length ?? 0);

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
