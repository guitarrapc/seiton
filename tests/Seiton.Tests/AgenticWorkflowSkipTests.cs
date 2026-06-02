using Seiton.Cli;
using Seiton.Commands;

namespace Seiton.Tests;

public sealed class AgenticWorkflowSkipTests
{
    [Test]
    public async Task AgenticWorkflowDetector_HasMetadataInPrefix_DetectsMarker()
    {
        var content = "# gh-aw-metadata: {\"version\":1}\nname: test\n"u8.ToArray();
        await Assert.That(AgenticWorkflowDetector.HasMetadataInPrefix(content)).IsTrue();
    }

    [Test]
    public async Task AgenticWorkflowDetector_HasMetadataInPrefix_DetectsMarkerWithLeadingWhitespace()
    {
        var content = "  \t# gh-aw-metadata: {}\n"u8.ToArray();
        await Assert.That(AgenticWorkflowDetector.HasMetadataInPrefix(content)).IsTrue();
    }

    [Test]
    public async Task AgenticWorkflowDetector_HasMetadataInPrefix_EmptyContent_ReturnsFalse()
    {
        await Assert.That(AgenticWorkflowDetector.HasMetadataInPrefix([])).IsFalse();
    }

    [Test]
    public async Task AgenticWorkflowDetector_HasMetadataInPrefix_NoMarker_ReturnsFalse()
    {
        var content = "name: CI\non: push\n"u8.ToArray();
        await Assert.That(AgenticWorkflowDetector.HasMetadataInPrefix(content)).IsFalse();
    }

    [Test]
    public async Task AgenticWorkflowDetector_HasMetadataInPrefix_SimilarCommentWithoutMarker_ReturnsFalse()
    {
        var content = "# gh-aw-metadata\nname: test\n"u8.ToArray();
        await Assert.That(AgenticWorkflowDetector.HasMetadataInPrefix(content)).IsFalse();
    }

    [Test]
    public async Task AgenticWorkflowDetector_HasMetadataInPrefix_IgnoresAfterTenthLine()
    {
        var lines = Enumerable.Repeat("# comment\n", 10).Append("# gh-aw-metadata: {}\n");
        var content = System.Text.Encoding.UTF8.GetBytes(string.Concat(lines));
        await Assert.That(AgenticWorkflowDetector.HasMetadataInPrefix(content)).IsFalse();
    }

    [Test]
    public async Task ResolveFiles_SkipAgenticWorkflows_ExcludesMarkedFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"seiton-test-{Guid.NewGuid():N}");
        var workflowsDir = Path.Combine(tempDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);
        File.WriteAllText(Path.Combine(workflowsDir, "ci.yml"), "on: push\n");
        File.WriteAllText(Path.Combine(workflowsDir, "agentic.lock.yml"), "# gh-aw-metadata: {}\nname: Agentic\n");

        try
        {
            using var sw = new StringWriter();
            var logger = VerboseLogger.Create(VerboseLevel.Summary, sw);
            var files = InputDiscovery.ResolveFiles([], includeActions: false, logger, skipAgenticWorkflows: true, startDirectory: tempDir);

            await Assert.That(files).Count().IsEqualTo(1);
            await Assert.That(files[0]).Contains("ci.yml");
            await Assert.That(sw.ToString()).Contains("skipped");
            await Assert.That(sw.ToString()).Contains("agentic workflow");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
