using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class DocumentKindClassificationTests
{
    [Test]
    public async Task PathHint_BasenameActionYml_IsActionMetadata()
    {
        var kind = DocumentKindClassifier.GetPathHintKind("action.yml");
        await Assert.That(kind).IsEqualTo(DocumentKind.ActionMetadata);
    }

    [Test]
    public async Task PathHint_GithubActionsPath_IsActionMetadata()
    {
        var kind = DocumentKindClassifier.GetPathHintKind(".github/actions/foo/action.yaml");
        await Assert.That(kind).IsEqualTo(DocumentKind.ActionMetadata);
    }

    [Test]
    public async Task ParseClassified_ActionMetadata_RunsOnly_NoWorkflowRequiredKeyErrors()
    {
        var yaml = """
        name: Sample action
        runs:
          using: composite
          steps:
            - run: echo hello
              shell: bash
        """;

        var result = WorkflowParser.ParseClassified(Encoding.UTF8.GetBytes(yaml), ".github/actions/sample/action.yml");

        await Assert.That(result.Classification.FinalKind).IsEqualTo(DocumentKind.ActionMetadata);
        await Assert.That(result.ParseResult.Diagnostics.Any(d => d.Message.Contains("required key 'on' is missing", StringComparison.Ordinal))).IsFalse();
        await Assert.That(result.ParseResult.Diagnostics.Any(d => d.Message.Contains("required key 'jobs' is missing", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ParseClassified_PathHintActionButWorkflowStructure_ReportsMismatch()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo hi
        """;

        var result = WorkflowParser.ParseClassified(Encoding.UTF8.GetBytes(yaml), ".github/actions/sample/action.yml");

        await Assert.That(result.Classification.FinalKind).IsEqualTo(DocumentKind.Workflow);
        await Assert.That(result.Classification.HasHintMismatch).IsTrue();
        await Assert.That(result.ParseResult.Diagnostics.Any(d => d.Message.Contains("path hint suggests action-metadata", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ParseClassified_JobsAndRuns_ReportsAmbiguity()
    {
        var yaml = """
        runs:
          using: composite
          steps:
            - run: echo hi
              shell: bash
        jobs: {}
        """;

        var result = WorkflowParser.ParseClassified(Encoding.UTF8.GetBytes(yaml), "ambiguous.yml");

        await Assert.That(result.Classification.FinalKind).IsEqualTo(DocumentKind.Unknown);
        await Assert.That(result.Classification.IsAmbiguous).IsTrue();
        await Assert.That(result.ParseResult.Diagnostics.Any(d => d.Message.Contains("document kind is ambiguous", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_ActionMetadataInput_DoesNotRunWorkflowRules()
    {
        var yaml = """
        name: Sample action
        runs:
          using: composite
          steps:
            - run: echo hello
              shell: bash
        """;

        var engine = new LintEngine();
        var lint = engine.Check(Encoding.UTF8.GetBytes(yaml), ".github/actions/sample/action.yml");

        await Assert.That(lint.Diagnostics).IsEmpty();
    }
}
