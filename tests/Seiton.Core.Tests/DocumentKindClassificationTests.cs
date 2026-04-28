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
        description: sample
        runs:
          using: composite
          steps:
            - run: echo hello
              shell: bash
        """;

        var result = WorkflowParser.ParseClassified(Encoding.UTF8.GetBytes(yaml), ".github/actions/sample/action.yml");

        await Assert.That(result.Classification.FinalKind).IsEqualTo(DocumentKind.ActionMetadata);
        await Assert.That(result.ParseResult.Workflow).IsNull();
        await Assert.That(result.ParseResult.ActionMetadata).IsNotNull();
        await Assert.That(result.ParseResult.ActionMetadata!.Runs).IsNotNull();
        await Assert.That(result.ParseResult.ActionMetadata.Runs!.Steps).IsNotNull();
        await Assert.That(result.ParseResult.ActionMetadata.Runs.Steps!.Count).IsEqualTo(1);
        await Assert.That(result.ParseResult.Diagnostics.Any(d => d.Message.Contains("\"on\" section is missing in workflow", StringComparison.Ordinal))).IsFalse();
        await Assert.That(result.ParseResult.Diagnostics.Any(d => d.Message.Contains("\"jobs\" section is missing in workflow", StringComparison.Ordinal))).IsFalse();
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
        description: sample
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

    // Action metadata required keys

    [Test]
    public async Task ParseClassified_ActionMetadata_MissingDescription_ReportsError()
    {
        var yaml = """
        name: My action
        runs:
          using: composite
          steps:
            - run: echo hello
              shell: bash
        """;

        var result = WorkflowParser.ParseClassified(Encoding.UTF8.GetBytes(yaml), "action.yml");
        await Assert.That(result.ParseResult.Diagnostics.Any(d => d.Message.Contains("description", StringComparison.OrdinalIgnoreCase) && d.Message.Contains("required", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task ParseClassified_ActionMetadata_MissingRuns_ReportsError()
    {
        var yaml = """
        name: My action
        description: some description
        inputs:
          name:
            description: your name
        """;

        var result = WorkflowParser.ParseClassified(Encoding.UTF8.GetBytes(yaml), "action.yml");
        await Assert.That(result.ParseResult.Diagnostics.Any(d => d.Message.Contains("runs", StringComparison.OrdinalIgnoreCase) && d.Message.Contains("required", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task ParseClassified_ActionMetadata_HasDescriptionAndRuns_NoDiagnostic()
    {
        var yaml = """
        name: My action
        description: does something
        runs:
          using: composite
          steps:
            - run: echo hello
              shell: bash
        """;

        var result = WorkflowParser.ParseClassified(Encoding.UTF8.GetBytes(yaml), "action.yml");
        await Assert.That(result.ParseResult.Diagnostics.Any(d => d.Message.Contains("required", StringComparison.OrdinalIgnoreCase))).IsFalse();
    }

    // Action metadata branding validation

    [Test]
    public async Task ParseClassified_ActionMetadata_InvalidBrandingColor_ReportsError()
    {
        var yaml = """
        name: My action
        description: does something
        branding:
          icon: edit
          color: gray-white
        runs:
          using: composite
          steps:
            - run: echo hello
              shell: bash
        """;

        var result = WorkflowParser.ParseClassified(Encoding.UTF8.GetBytes(yaml), "action.yml");
        await Assert.That(result.ParseResult.Diagnostics.Any(d => d.Message.Contains("gray-white", StringComparison.Ordinal) && d.Message.Contains("color", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task ParseClassified_ActionMetadata_InvalidBrandingIcon_ReportsError()
    {
        var yaml = """
        name: My action
        description: does something
        branding:
          icon: dog
          color: white
        runs:
          using: composite
          steps:
            - run: echo hello
              shell: bash
        """;

        var result = WorkflowParser.ParseClassified(Encoding.UTF8.GetBytes(yaml), "action.yml");
        await Assert.That(result.ParseResult.Diagnostics.Any(d => d.Message.Contains("dog", StringComparison.Ordinal) && d.Message.Contains("icon", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task ParseClassified_ActionMetadata_ValidBranding_NoDiagnostic()
    {
        var yaml = """
        name: My action
        description: does something
        branding:
          icon: edit
          color: white
        runs:
          using: composite
          steps:
            - run: echo hello
              shell: bash
        """;

        var result = WorkflowParser.ParseClassified(Encoding.UTF8.GetBytes(yaml), "action.yml");
        await Assert.That(result.ParseResult.Diagnostics.Any(d => d.Message.Contains("branding", StringComparison.OrdinalIgnoreCase))).IsFalse();
    }
}
