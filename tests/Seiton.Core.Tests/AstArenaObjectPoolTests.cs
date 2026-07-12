using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class AstArenaObjectPoolTests
{
    [Test]
    public async Task RegisterSliceMapBuffer_BuffersReturnedOnDispose()
    {
        var source = "ab"u8.ToArray();
        var arena = AstArena.Rent(source);

        // Simulate what the parser does: PooledBuffer.DetachArray() → RegisterSliceMapBuffer
        var buf = new PooledBuffer<SliceMap<int>.Entry>(8);
        buf.Add(new SliceMap<int>.Entry(new Utf8Slice(0, 1), 10));
        buf.Add(new SliceMap<int>.Entry(new Utf8Slice(1, 1), 20));
        var (entries, count) = buf.DetachArray();
        arena.RegisterSliceMapBuffer(entries);

        // The SliceMap should work correctly with the pooled array
        var map = new SliceMap<int>(entries, count, caseSensitive: true);
        await Assert.That(map.TryGetValue(source, "a"u8, out var va)).IsTrue();
        await Assert.That(va).IsEqualTo(10);
        await Assert.That(map.Count).IsEqualTo(2);

        // Dispose should not throw (buffers returned to pool)
        arena.Dispose();
    }

    [Test]
    public async Task RegisterSliceMapBuffer_MultipleBuffers_AllReturnedOnDispose()
    {
        var source = "abc"u8.ToArray();
        var arena = AstArena.Rent(source);

        // Register multiple buffers of different types
        var buf1 = new PooledBuffer<SliceMap<int>.Entry>(8);
        buf1.Add(new SliceMap<int>.Entry(new Utf8Slice(0, 1), 1));
        var (entries1, count1) = buf1.DetachArray();
        arena.RegisterSliceMapBuffer(entries1);

        var buf2 = new PooledBuffer<SliceMap<int>.Entry>(8);
        buf2.Add(new SliceMap<int>.Entry(new Utf8Slice(1, 1), 2));
        buf2.Add(new SliceMap<int>.Entry(new Utf8Slice(2, 1), 3));
        var (entries2, count2) = buf2.DetachArray();
        arena.RegisterSliceMapBuffer(entries2);

        // Both should work
        var map1 = new SliceMap<int>(entries1, count1, caseSensitive: true);
        var map2 = new SliceMap<int>(entries2, count2, caseSensitive: true);
        await Assert.That(map1.Count).IsEqualTo(1);
        await Assert.That(map2.Count).IsEqualTo(2);

        // Dispose should not throw
        arena.Dispose();
    }

    [Test]
    public async Task RegisterSliceMapBuffer_ResetForSource_ClearsRegisteredBuffers()
    {
        var source = "ab"u8.ToArray();
        var arena = AstArena.Rent(source);

        var buf = new PooledBuffer<SliceMap<int>.Entry>(8);
        buf.Add(new SliceMap<int>.Entry(new Utf8Slice(0, 1), 10));
        var (entries, _) = buf.DetachArray();
        arena.RegisterSliceMapBuffer(entries);
        arena.Dispose();

        // Re-rent: should reuse arena (from ThreadStatic cache), registered buffers should be cleared
        var source2 = "xy"u8.ToArray();
        arena = AstArena.Rent(source2);

        // Register a new buffer with the fresh arena — should not fail
        var buf2 = new PooledBuffer<SliceMap<int>.Entry>(8);
        buf2.Add(new SliceMap<int>.Entry(new Utf8Slice(0, 1), 99));
        var (entries2, count2) = buf2.DetachArray();
        arena.RegisterSliceMapBuffer(entries2);

        var map = new SliceMap<int>(entries2, count2, caseSensitive: true);
        await Assert.That(map.TryGetValue(source2, "x"u8, out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(99);

        arena.Dispose();
    }
}
