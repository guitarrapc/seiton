using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class ScalarHelpersTests
{
    [Test]
    public async Task ParseString_Scalar_ReturnsNode()
    {
        var reader = CreateReader("hello"u8, new[]
        {
            Scalar(0, 5, ScalarTag.Str),
        });
        var diagnostics = new List<Diagnostic>();

        var node = WorkflowParser.ParseString(ref reader, diagnostics, "expected string");

        await Assert.That(node is not null).IsTrue();
        await Assert.That(node!.Value.Length).IsEqualTo(5);
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task ParseBool_BoolTag_ReturnsValue()
    {
        var reader = CreateReader("true"u8, new[]
        {
            Scalar(0, 4, ScalarTag.Bool),
        });
        var diagnostics = new List<Diagnostic>();

        var node = WorkflowParser.ParseBool(ref reader, diagnostics, "expected bool");

        await Assert.That(node is not null).IsTrue();
        await Assert.That(node!.Value).IsTrue();
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task ParseInt_IntTag_ReturnsValue()
    {
        var reader = CreateReader("123"u8, new[]
        {
            Scalar(0, 3, ScalarTag.Int),
        });
        var diagnostics = new List<Diagnostic>();

        var node = WorkflowParser.ParseInt(ref reader, diagnostics, "expected int");

        await Assert.That(node is not null).IsTrue();
        await Assert.That(node!.Value).IsEqualTo(123);
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task ParseFloat_FloatTag_ReturnsValue()
    {
        var reader = CreateReader("1.5"u8, new[]
        {
            Scalar(0, 3, ScalarTag.Float),
        });
        var diagnostics = new List<Diagnostic>();

        var node = WorkflowParser.ParseFloat(ref reader, diagnostics, "expected float");

        await Assert.That(node is not null).IsTrue();
        await Assert.That(node!.Value).IsEqualTo(1.5d);
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task ParseExpression_WholeExpression_Validates()
    {
        var reader = CreateReader("github.ref"u8, new[]
        {
            Scalar(0, 10, ScalarTag.Str),
        });
        var diagnostics = new List<Diagnostic>();

        var node = WorkflowParser.ParseExpression(ref reader, diagnostics, ExpressionValidationContext.Workflow, "expected expression");

        await Assert.That(node is not null).IsTrue();
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task MayParseExpression_EmbeddedExpression_ReturnsNode()
    {
        var source = "prefix-${{ github.ref }}"u8;
        var reader = CreateReader(source, new[]
        {
            Scalar(0, source.Length, ScalarTag.Str),
        });
        var diagnostics = new List<Diagnostic>();

        var node = WorkflowParser.MayParseExpression(ref reader, diagnostics, ExpressionValidationContext.Workflow);

        await Assert.That(node is not null).IsTrue();
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task ParseStringOrStringSequence_Sequence_ReturnsAll()
    {
        var source = "ab"u8;
        var reader = CreateReader(source, new[]
        {
            Event(YamlEventKind.SequenceStart),
            Scalar(0, 1, ScalarTag.Str),
            Scalar(1, 1, ScalarTag.Str),
            Event(YamlEventKind.SequenceEnd),
        });
        var diagnostics = new List<Diagnostic>();

        var nodes = WorkflowParser.ParseStringOrStringSequence(ref reader, diagnostics, "expected sequence");

        await Assert.That(nodes.Length).IsEqualTo(2);
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task ParseBool_WrongTag_ReportsError()
    {
        var reader = CreateReader("not-bool"u8, new[]
        {
            Scalar(0, 8, ScalarTag.Str),
        });
        var diagnostics = new List<Diagnostic>();

        var node = WorkflowParser.ParseBool(ref reader, diagnostics, "expected bool");

        await Assert.That(node).IsNull();
        await Assert.That(diagnostics.Count).IsEqualTo(1);
    }

    private static FakeYamlStreamReader CreateReader(ReadOnlySpan<byte> source, FakeYamlStreamReader.FakeEvent[] events)
    {
        return new FakeYamlStreamReader(events, source.ToArray());
    }

    private static FakeYamlStreamReader.FakeEvent Scalar(int offset, int length, ScalarTag tag)
    {
        return new FakeYamlStreamReader.FakeEvent(
            YamlEventKind.Scalar,
            new Utf8Slice(offset, length),
            new TextPosition(offset, 1, offset + 1),
            new TextPosition(offset + length, 1, offset + length + 1),
            tag,
            Quoted: false);
    }

    private static FakeYamlStreamReader.FakeEvent Event(YamlEventKind kind)
    {
        return new FakeYamlStreamReader.FakeEvent(
            kind,
            default,
            new TextPosition(0, 1, 1),
            new TextPosition(0, 1, 1));
    }
}
