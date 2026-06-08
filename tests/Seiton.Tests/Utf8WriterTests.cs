using System.Buffers;
using Seiton.Output;

namespace Seiton.Tests;

public sealed class Utf8WriterTests
{
    [Test]
    public async Task WriteUtf8_PartialBufferWriter_EncodesFullPayload()
    {
        var destination = new MaxChunkBufferWriter(maxChunkSize: 8);
        var writer = new Utf8Writer(destination);
        var payload = new string('x', 40) + "日本語";
        writer.WriteUtf8(payload);

        await Assert.That(Encoding.UTF8.GetString(destination.WrittenSpan)).IsEqualTo(payload);
    }

    [Test]
    public async Task WriteLiteral_PartialBufferWriter_WritesFullPayload()
    {
        var destination = new MaxChunkBufferWriter(maxChunkSize: 4);
        var writer = new Utf8Writer(destination);
        var payload = Encoding.UTF8.GetBytes("0123456789abcdef");
        writer.WriteLiteral(payload);

        await Assert.That(destination.WrittenSpan.SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task Write_NonAsciiChar_EncodesUtf8()
    {
        var destination = new ArrayBufferWriter<byte>();
        var writer = new Utf8Writer(destination);
        writer.Write('日');
        writer.Write('本');

        await Assert.That(Encoding.UTF8.GetString(destination.WrittenSpan)).IsEqualTo("日本");
    }

    [Test]
    public async Task WriteRepeated_PartialBufferWriter_FillsFullCount()
    {
        var destination = new MaxChunkBufferWriter(maxChunkSize: 6);
        var writer = new Utf8Writer(destination);
        writer.WriteRepeated((byte)'-', 25);

        await Assert.That(Encoding.UTF8.GetString(destination.WrittenSpan)).IsEqualTo(new string('-', 25));
    }

    [Test]
    public async Task WriteLine_PipeChar_EmitsCharacterNotAsciiCode()
    {
        var destination = new ArrayBufferWriter<byte>();
        var writer = new Utf8Writer(destination);
        writer.WriteLine('|');

        var output = Encoding.UTF8.GetString(destination.WrittenSpan).ReplaceLineEndings("\n");
        await Assert.That(output).IsEqualTo("|\n");
        await Assert.That(output).DoesNotContain("124");
    }

    [Test]
    public async Task WriteLine_Char_MatchesWriteThenNewLine()
    {
        var destination = new ArrayBufferWriter<byte>();
        var writer = new Utf8Writer(destination);
        writer.Write('x');
        writer.WriteNewLine();

        var expected = Encoding.UTF8.GetString(destination.WrittenSpan);

        destination = new ArrayBufferWriter<byte>();
        writer = new Utf8Writer(destination);
        writer.WriteLine('x');

        await Assert.That(Encoding.UTF8.GetString(destination.WrittenSpan)).IsEqualTo(expected);
    }

    [Test]
    public async Task WriteLine_Int_StillEmitsDecimalNotCharCode()
    {
        var destination = new ArrayBufferWriter<byte>();
        var writer = new Utf8Writer(destination);
        writer.WriteLine(124);

        var output = Encoding.UTF8.GetString(destination.WrittenSpan).ReplaceLineEndings("\n");
        await Assert.That(output).IsEqualTo("124\n");
    }

    /// <summary>
    /// Caps each <see cref="GetSpan"/> grant to exercise chunked writes through <see cref="IBufferWriter{T}"/>.
    /// </summary>
    private sealed class MaxChunkBufferWriter(int maxChunkSize) : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _inner = new();

        public ReadOnlySpan<byte> WrittenSpan => _inner.WrittenSpan;

        public void Advance(int count) => _inner.Advance(count);

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var memory = _inner.GetMemory(Math.Max(sizeHint, maxChunkSize));
            return memory[..Math.Min(memory.Length, maxChunkSize)];
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (_inner.FreeCapacity == 0)
            {
                _inner.GetSpan(Math.Max(sizeHint, maxChunkSize));
            }

            var free = _inner.GetSpan(Math.Max(sizeHint, maxChunkSize));
            return free[..Math.Min(free.Length, maxChunkSize)];
        }
    }
}
