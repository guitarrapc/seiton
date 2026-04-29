using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class WebhookTypesGeneratedTests
{
    [Test]
    public async Task Parse_KnownWebhookTypesValidation_UsesGeneratedTable()
    {
        var yaml = """
        on:
          pull_request:
            types: [opened, not-a-valid-type]
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "webhook-generated-known.yml");

        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unsupported activity type: not-a-valid-type", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_UnknownWebhookEvent_ReportsDiagnostic()
    {
        var yaml = """
        on: not_existing_event
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "webhook-generated-unknown.yml");

        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown event \"not_existing_event\"", StringComparison.Ordinal) && x.Message.Contains("see https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows", StringComparison.Ordinal))).IsTrue();
    }
}
