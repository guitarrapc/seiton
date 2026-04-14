using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class ParserAdapterResilienceTests
{
    [Test]
    public async Task ParseWithReader_FakeReader_MinimalWorkflow_Parses()
    {
        var source = Encoding.UTF8.GetBytes("onpushjobs");
        var events = new[]
        {
            Event(YamlEventKind.StreamStart),
            Event(YamlEventKind.DocumentStart),
            Event(YamlEventKind.MappingStart),
            Scalar(0, 2), // on
            Scalar(2, 4), // push
            Scalar(6, 4), // jobs
            Event(YamlEventKind.MappingStart),
            Event(YamlEventKind.MappingEnd),
            Event(YamlEventKind.MappingEnd),
            Event(YamlEventKind.DocumentEnd),
            Event(YamlEventKind.StreamEnd),
        };

        var reader = new FakeYamlStreamReader(events, source);
        var result = WorkflowParser.ParseWithReader(ref reader, source);

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task ParseWithReader_FakeReader_DuplicateWorkflowKey_ReportsError()
    {
        var source = Encoding.UTF8.GetBytes("onpushonpushjobs");
        var events = new[]
        {
            Event(YamlEventKind.MappingStart),
            Scalar(0, 2),  // on
            Scalar(2, 4),  // push
            Scalar(6, 2),  // on (duplicate)
            Scalar(8, 4),  // push
            Scalar(12, 4), // jobs
            Event(YamlEventKind.MappingStart),
            Event(YamlEventKind.MappingEnd),
            Event(YamlEventKind.MappingEnd),
        };

        var reader = new FakeYamlStreamReader(events, source);
        var result = WorkflowParser.ParseWithReader(ref reader, source);

        await Assert.That(result.Diagnostics.Any(static x => x.Message.Contains("workflow contains duplicate key: on", StringComparison.Ordinal))).IsTrue();
    }

    private static FakeYamlStreamReader.FakeEvent Scalar(int offset, int length)
    {
        return new FakeYamlStreamReader.FakeEvent(
            YamlEventKind.Scalar,
            new Utf8Slice(offset, length),
            new TextPosition(offset, 1, offset + 1),
            new TextPosition(offset + length, 1, offset + length + 1),
            ScalarTag.Str,
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
