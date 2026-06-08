using System.Buffers;
using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;
using Seiton.Output;

namespace Seiton.Tests;

public sealed class StructureSnippetTests
{
    private const string WorkflowYaml = """
        name: test
        on: push
        jobs:
          examples:
            runs-on: ubuntu-24.04
            permissions: {}
            steps:
              - uses: actions/checkout@v2
                with:
                  fetch-depth: 1
        """;

    private const string ActionYaml = """
        name: test
        runs:
          using: composite
          steps:
            - run: echo hi
        """;

    [Test]
    public async Task Rich_UnpinnedUses_ShowsMinimalStructureSkeleton()
    {
        var path = "ci.yml";
        var bytes = Encoding.UTF8.GetBytes(WorkflowYaml);
        using var result = new LintEngine().Check(bytes, path);
        var diag = result.Diagnostics.First(d => d.RuleId == "unpinned-uses");

        var output = RenderRich(diag, bytes, path);

        await Assert.That(output).Contains("= structure:");
        await Assert.That(output).Contains("jobs:");
        await Assert.That(output).Contains("examples:");
        await Assert.That(output).Contains("steps:");
        await Assert.That(output).Contains("- uses: actions/checkout@v2");
        await Assert.That(output).Contains("...");
        await Assert.That(output).DoesNotContain("runs-on:");
        await Assert.That(output).DoesNotContain("permissions:");
        await Assert.That(output).DoesNotContain("fetch-depth:");
    }

    [Test]
    public async Task Rich_ParserStepMessage_ShowsStructureFromMessagePath()
    {
        var yaml = """
            on: push
            jobs:
              build:
                runs-on: ubuntu-24.04
                steps:
                  - uses: actions/checkout@deadbeefdeadbeefdeadbeefdeadbeefdeadbeef
            """;
        var path = "ci.yml";
        var bytes = Encoding.UTF8.GetBytes(yaml);
        var diag = new Diagnostic(
            DiagnosticSeverity.Error,
            "jobs.'build'.steps[1].uses must be string",
            new TextRange(0, 0, 6, 11, 6, 80),
            RuleId: "parse",
            FilePath: path);

        var output = RenderRich(diag, bytes, path);

        await Assert.That(output).Contains("= structure:");
        await Assert.That(output).Contains("jobs:");
        await Assert.That(output).Contains("build:");
        await Assert.That(output).Contains("steps:");
    }

    [Test]
    public async Task Rich_ActionMetadata_RunStep_ShowsStructure()
    {
        var path = "action.yml";
        var bytes = Encoding.UTF8.GetBytes(ActionYaml);
        var diag = new Diagnostic(
            DiagnosticSeverity.Error,
            "shell is required if run is set",
            new TextRange(0, 0, 5, 5, 5, 18),
            RuleId: "action-shell-is-required",
            FilePath: path);

        var output = RenderRich(diag, bytes, path);

        await Assert.That(output).Contains("= structure:");
        await Assert.That(output).Contains("runs:");
        await Assert.That(output).Contains("steps:");
        await Assert.That(output).Contains("- run: echo hi");
    }

    [Test]
    public async Task Oneline_OmitsStructureBlock()
    {
        var path = "ci.yml";
        var bytes = Encoding.UTF8.GetBytes(WorkflowYaml);
        using var result = new LintEngine().Check(bytes, path);
        var diag = result.Diagnostics.First(d => d.RuleId == "unpinned-uses");

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(
            writer,
            [diag],
            OutputFormat.Text,
            oneline: true,
            color: false,
            new Dictionary<string, byte[]> { [path] = bytes });
        writer.Flush();

        await Assert.That(sb.ToString()).DoesNotContain("= structure:");
    }

    [Test]
    public async Task Json_OmitsStructureBlock()
    {
        var path = "ci.yml";
        var bytes = Encoding.UTF8.GetBytes(WorkflowYaml);
        using var result = new LintEngine().Check(bytes, path);
        var diag = result.Diagnostics.First(d => d.RuleId == "unpinned-uses");

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        DiagnosticFormatter.WriteToTextWriter(
            writer,
            [diag],
            OutputFormat.Json,
            oneline: false,
            color: false,
            new Dictionary<string, byte[]> { [path] = bytes });
        writer.Flush();

        await Assert.That(sb.ToString()).DoesNotContain("structure");
        await Assert.That(sb.ToString()).DoesNotContain("jobs:");
    }

    private static string RenderRich(Diagnostic diagnostic, byte[] source, string path)
    {
        var buffer = new ArrayBufferWriter<byte>();
        DiagnosticFormatter.Write(
            buffer,
            [diagnostic],
            OutputFormat.Text,
            oneline: false,
            color: false,
            new Dictionary<string, byte[]> { [path] = source });
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
