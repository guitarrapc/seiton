using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class SliceMapTests
{
    [Test]
    public async Task Count_OversizedArray_ReturnsLogicalCount()
    {
        // Arrange: array has 8 slots but only 2 are valid
        var source = "ab"u8.ToArray();
        var entries = new SliceMap<int>.Entry[8];
        entries[0] = new SliceMap<int>.Entry(new Utf8Slice(0, 1), 10);
        entries[1] = new SliceMap<int>.Entry(new Utf8Slice(1, 1), 20);

        // Act: construct with count=2, not array.Length=8
        var map = new SliceMap<int>(entries, count: 2, caseSensitive: true);

        // Assert
        await Assert.That(map.Count).IsEqualTo(2);
    }

    [Test]
    public async Task TryGetValue_OversizedArray_OnlySearchesValidEntries()
    {
        var source = "abc"u8.ToArray();
        var entries = new SliceMap<int>.Entry[8];
        entries[0] = new SliceMap<int>.Entry(new Utf8Slice(0, 1), 10); // "a"
        entries[1] = new SliceMap<int>.Entry(new Utf8Slice(1, 1), 20); // "b"
        // entries[2] is beyond count — should not be found even if it has data
        entries[2] = new SliceMap<int>.Entry(new Utf8Slice(2, 1), 30); // "c"

        var map = new SliceMap<int>(entries, count: 2, caseSensitive: true);

        // "a" and "b" should be found
        await Assert.That(map.TryGetValue(source, "a"u8, out var va)).IsTrue();
        await Assert.That(va).IsEqualTo(10);
        await Assert.That(map.TryGetValue(source, "b"u8, out var vb)).IsTrue();
        await Assert.That(vb).IsEqualTo(20);

        // "c" is at index 2 but beyond count=2, should not be found
        await Assert.That(map.TryGetValue(source, "c"u8, out _)).IsFalse();
    }

    [Test]
    public async Task Entries_OversizedArray_ReturnsOnlyValidSlice()
    {
        var source = "ab"u8.ToArray();
        var entries = new SliceMap<int>.Entry[8];
        entries[0] = new SliceMap<int>.Entry(new Utf8Slice(0, 1), 10);
        entries[1] = new SliceMap<int>.Entry(new Utf8Slice(1, 1), 20);

        var map = new SliceMap<int>(entries, count: 2, caseSensitive: true);

        await Assert.That(map.Entries.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Enumerator_OversizedArray_IteratesOnlyValidEntries()
    {
        var source = "ab"u8.ToArray();
        var entries = new SliceMap<int>.Entry[8];
        entries[0] = new SliceMap<int>.Entry(new Utf8Slice(0, 1), 10);
        entries[1] = new SliceMap<int>.Entry(new Utf8Slice(1, 1), 20);

        var map = new SliceMap<int>(entries, count: 2, caseSensitive: true);

        var count = 0;
        foreach (var entry in map)
        {
            count++;
        }

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task Constructor_CountExceedsArrayLength_ThrowsArgumentOutOfRangeException()
    {
        var entries = new SliceMap<int>.Entry[2];

        await Assert.That(() => new SliceMap<int>(entries, count: 3, caseSensitive: true))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_NegativeCount_ThrowsArgumentOutOfRangeException()
    {
        var entries = new SliceMap<int>.Entry[2];

        await Assert.That(() => new SliceMap<int>(entries, count: -1, caseSensitive: true))
            .Throws<ArgumentOutOfRangeException>();
    }
}
