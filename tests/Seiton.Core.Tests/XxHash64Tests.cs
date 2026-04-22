using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class XxHash64Tests
{
    // Official XXH64 reference test vectors (seed = 0)
    // Source: https://github.com/Cyan4973/xxHash/blob/dev/cli/xsum_sanity_check.c

    [Test]
    public async Task Hash_EmptyInput_MatchesReferenceVector()
    {
        var result = XxHash64.Hash(ReadOnlySpan<byte>.Empty);
        await Assert.That(result).IsEqualTo(0xEF46DB3751D8E999UL);
    }

    [Test]
    [Arguments(new byte[] { 0 }, 0xE934A84ADB052768UL)]
    [Arguments(new byte[] { 0, 0, 0, 0 }, 0x3AEFA6FD5CF2DEB4UL)]
    public async Task Hash_ShortInputs_MatchReferenceVectors(byte[] data, ulong expected)
    {
        var result = XxHash64.Hash(data);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task Hash_14Bytes_MatchesReferenceVector()
    {
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
        var result = XxHash64.Hash(data);
        await Assert.That(result).IsEqualTo(0x5CDA8B69BBFC1D45UL);
    }

    [Test]
    public async Task Hash_WithSeed_ProducesDifferentResult()
    {
        var data = "hello world"u8;
        var h0 = XxHash64.Hash(data, seed: 0);
        var h1 = XxHash64.Hash(data, seed: 42);
        await Assert.That(h0).IsNotEqualTo(h1);
    }

    [Test]
    public async Task Hash32_ReturnsTruncatedLower32Bits()
    {
        var data = "test"u8;
        var h64 = XxHash64.Hash(data);
        var h32 = XxHash64.Hash32(data);
        await Assert.That(h32).IsEqualTo(unchecked((int)h64));
    }

    [Test]
    public async Task Hash_IdenticalInputs_ProduceSameResult()
    {
        var a = "workflow_dispatch"u8;
        var b = "workflow_dispatch"u8;
        await Assert.That(XxHash64.Hash(a)).IsEqualTo(XxHash64.Hash(b));
    }

    [Test]
    public async Task Hash_DifferentInputs_ProduceDifferentResults()
    {
        var a = "push"u8;
        var b = "pull_request"u8;
        await Assert.That(XxHash64.Hash(a)).IsNotEqualTo(XxHash64.Hash(b));
    }

    [Test]
    public async Task Hash_32ByteInput_ExercisesMainLoop()
    {
        // Exactly 32 bytes — exercises the 4-lane accumulator path
        var data = new byte[32];
        for (var i = 0; i < 32; i++) data[i] = (byte)i;
        var result = XxHash64.Hash(data);
        // Just verify it doesn't throw and produces a non-zero value
        await Assert.That(result).IsNotEqualTo(0UL);
    }

    [Test]
    public async Task Hash_64ByteInput_ExercisesMultipleRounds()
    {
        // 64 bytes — exercises two rounds of the 4-lane accumulator
        var data = new byte[64];
        for (var i = 0; i < 64; i++) data[i] = (byte)i;
        var result = XxHash64.Hash(data);
        await Assert.That(result).IsNotEqualTo(0UL);
    }
}
