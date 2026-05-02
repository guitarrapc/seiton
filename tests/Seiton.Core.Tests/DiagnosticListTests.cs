using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public class DiagnosticListTests
{
    [Test]
    public async Task FromArray_Length_MatchesArrayLength()
    {
        var diags = new Diagnostic[] { new(DiagnosticSeverity.Error, "test", default) };
        DiagnosticList list = diags;
        await Assert.That(list.Length).IsEqualTo(1);
    }

    [Test]
    public async Task FromArray_Indexer_ReturnsCorrectElement()
    {
        var diags = new Diagnostic[]
        {
            new(DiagnosticSeverity.Error, "first", default),
            new(DiagnosticSeverity.Warning, "second", default),
        };
        DiagnosticList list = diags;
        await Assert.That(list[0].Message).IsEqualTo("first");
        await Assert.That(list[1].Message).IsEqualTo("second");
    }

    [Test]
    public async Task AsSpan_ReturnsValidSlice()
    {
        var diags = new Diagnostic[] { new(DiagnosticSeverity.Error, "msg", default) };
        DiagnosticList list = diags;
        var span = list.AsSpan();
        var length = span.Length;
        var msg = span[0].Message;
        await Assert.That(length).IsEqualTo(1);
        await Assert.That(msg).IsEqualTo("msg");
    }

    [Test]
    public async Task SupportsLinq_Any()
    {
        var diags = new Diagnostic[]
        {
            new(DiagnosticSeverity.Error, "a", default),
            new(DiagnosticSeverity.Warning, "b", default),
        };
        DiagnosticList list = diags;
        await Assert.That(list.Any(d => d.Message == "a")).IsTrue();
        await Assert.That(list.Count(d => d.Severity == DiagnosticSeverity.Warning)).IsEqualTo(1);
    }

    [Test]
    public async Task PooledArray_ExposesOnlyValidPortion()
    {
        // Simulates a pooled array that's larger than the actual count
        var largeArray = new Diagnostic[16];
        largeArray[0] = new(DiagnosticSeverity.Error, "first", default);
        largeArray[1] = new(DiagnosticSeverity.Warning, "second", default);
        var list = new DiagnosticList(largeArray, 2);

        await Assert.That(list.Length).IsEqualTo(2);
        await Assert.That(list.AsSpan().Length).IsEqualTo(2);
        await Assert.That(list.Count()).IsEqualTo(2);
        await Assert.That(list[0].Message).IsEqualTo("first");
        await Assert.That(list[1].Message).IsEqualTo("second");
    }

    [Test]
    public async Task Foreach_IteratesOnlyValidElements()
    {
        var largeArray = new Diagnostic[8];
        largeArray[0] = new(DiagnosticSeverity.Error, "one", default);
        largeArray[1] = new(DiagnosticSeverity.Warning, "two", default);
        var list = new DiagnosticList(largeArray, 2);

        var messages = new List<string>();
        foreach (var d in list)
        {
            messages.Add(d.Message);
        }

        await Assert.That(messages).Count().IsEqualTo(2);
        await Assert.That(messages[0]).IsEqualTo("one");
        await Assert.That(messages[1]).IsEqualTo("two");
    }

    [Test]
    public async Task Empty_HasZeroLength()
    {
        DiagnosticList list = [];
        await Assert.That(list.Length).IsEqualTo(0);
        await Assert.That(list.AsSpan().Length).IsEqualTo(0);
        await Assert.That(list.Any()).IsFalse();
    }

    [Test]
    public async Task ImplicitConversion_FromEmptyArray()
    {
        DiagnosticList list = Array.Empty<Diagnostic>();
        await Assert.That(list.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ParseResult_Diagnostics_IsDiagnosticList()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi"u8.ToArray();
        var result = WorkflowParser.ParseClassified(yaml, "test.yml").ParseResult;
        var diags = result.Diagnostics;
        // Should support both span and LINQ access
        var span = diags.AsSpan();
        await Assert.That(span.Length).IsEqualTo(diags.Length);
    }
}
