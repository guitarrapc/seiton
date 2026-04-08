using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class Utf8StringTests
{
    [Test]
    public async Task Utf8String_EqualityAndHash_AreStableForSameBytes()
    {
        var a = new Utf8String("workflow_dispatch"u8);
        var b = new Utf8String("workflow_dispatch"u8);

        await Assert.That(a == b).IsTrue();
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task Utf8String_Equality_IsByteSensitive()
    {
        var a = new Utf8String("Push"u8);
        var b = new Utf8String("push"u8);

        await Assert.That(a == b).IsFalse();
        await Assert.That(a.Equals(b)).IsFalse();
    }

    [Test]
    public async Task Utf8String_FromLowerAscii_NormalizesOnlyAsciiUppercase()
    {
        var normalized = Utf8String.FromLowerAscii("JoB_ID-123"u8);

        var text = Encoding.UTF8.GetString(normalized.Span);
        await Assert.That(text).IsEqualTo("job_id-123");
    }

    [Test]
    public async Task Utf8Slice_ToUtf8String_CopiesReferencedRange()
    {
        var source = Encoding.UTF8.GetBytes("abc:workflow_call:def");
        var slice = new Utf8Slice(4, 13);

        var utf8 = slice.ToUtf8String(source);

        await Assert.That(Encoding.UTF8.GetString(utf8.Span)).IsEqualTo("workflow_call");
    }
}
