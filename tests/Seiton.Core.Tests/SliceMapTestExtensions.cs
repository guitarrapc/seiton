using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

/// <summary>
/// Test helper extensions for SliceMap to simplify migration from Dictionary-based access patterns.
/// </summary>
internal static class SliceMapTestExtensions
{
    /// <summary>
    /// Gets a value by UTF-8 key. Throws KeyNotFoundException if not found.
    /// Replaces dictionary indexer pattern: map[key].
    /// </summary>
    public static TValue Get<TValue>(this SliceMap<TValue> map, ReadOnlySpan<byte> source, ReadOnlySpan<byte> key)
    {
        if (map.TryGetValue(source, key, out var value))
            return value;
        throw new KeyNotFoundException($"Key '{Encoding.UTF8.GetString(key)}' not found in SliceMap");
    }

    /// <summary>
    /// Enumerates all values in the SliceMap.
    /// Replaces .Values property pattern.
    /// </summary>
    public static IEnumerable<TValue> Values<TValue>(this SliceMap<TValue> map)
    {
        foreach (var entry in map)
            yield return entry.Value;
    }

    /// <summary>
    /// Creates a SliceMap from (Utf8String key, T value) pairs with a synthetic source buffer.
    /// Returns both the map and its source bytes.
    /// For hand-constructing test data.
    /// </summary>
    public static (SliceMap<T> Map, byte[] Source) CreateSliceMap<T>(params (Utf8String Key, T Value)[] items)
    {
        var totalLen = 0;
        for (var i = 0; i < items.Length; i++)
            totalLen += items[i].Key.Span.Length;

        var source = new byte[Math.Max(totalLen, 1)];
        var entries = new SliceMap<T>.Entry[items.Length];
        var offset = 0;
        for (var i = 0; i < items.Length; i++)
        {
            var keySpan = items[i].Key.Span;
            keySpan.CopyTo(source.AsSpan(offset));
            entries[i] = new SliceMap<T>.Entry(new Utf8Slice(offset, keySpan.Length), items[i].Value);
            offset += keySpan.Length;
        }

        return (new SliceMap<T>(entries, caseSensitive: false), source);
    }
}
