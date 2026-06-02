using Seiton.Output;

namespace Seiton.Tests;

public sealed class PooledByteBufferWriterTests
{
    [Test]
    public async Task GetSpan_Advance_WritesExpectedBytes()
    {
        using var writer = new PooledByteBufferWriter(8);
        var span = writer.GetSpan(4);
        span[0] = (byte)'t';
        span[1] = (byte)'e';
        span[2] = (byte)'s';
        span[3] = (byte)'t';
        writer.Advance(4);

        await Assert.That(writer.WrittenSpan.SequenceEqual("test"u8)).IsTrue();
    }

    [Test]
    public async Task GetSpan_GrowsBuffer_PreservesExistingBytes()
    {
        using var writer = new PooledByteBufferWriter(4);
        var first = writer.GetSpan(4);
        first[0] = 1;
        first[1] = 2;
        first[2] = 3;
        first[3] = 4;
        writer.Advance(4);

        var second = writer.GetSpan(64);
        second[0] = 5;
        second[1] = 6;
        writer.Advance(2);

        await Assert.That(writer.WrittenSpan.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 })).IsTrue();
    }

    [Test]
    public async Task DisposedWriter_ThrowsOnFurtherUse()
    {
        var writer = new PooledByteBufferWriter(8);
        writer.Dispose();

        await Assert.That(() => writer.GetSpan(1)).Throws<ObjectDisposedException>();
        await Assert.That(() => writer.GetMemory(1)).Throws<ObjectDisposedException>();
        await Assert.That(() => writer.Advance(1)).Throws<ObjectDisposedException>();
    }
}
