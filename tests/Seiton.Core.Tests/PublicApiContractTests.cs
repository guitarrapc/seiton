using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

/// <summary>
/// Tests for the public API contract: parser-only, linter-only, and combined use cases.
/// Verifies that callers can use ParseResult with LintEngine.Check for pre-parsed linting.
/// </summary>
public sealed class PublicApiContractTests
{
    [Test]
    public async Task ParseResult_ThenLint_ProducesSameResults()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ unknownFunc() }}\n"u8.ToArray();
        var filePath = "contract.yml";

        // Combined (existing API)
        using var combinedResult = new LintEngine().Check(yaml, filePath);

        // Separate: parse then lint (new API)
        using var parseResult = WorkflowParser.Parse(yaml, filePath);
        using var lintResult = new LintEngine().Check(parseResult, yaml, filePath);

        // Both should contain the unknown function diagnostic
        await Assert.That(combinedResult.Diagnostics.Any(d => d.Message.Contains("unknown expression function", StringComparison.Ordinal))).IsTrue();
        await Assert.That(lintResult.Diagnostics.Any(d => d.Message.Contains("unknown expression function", StringComparison.Ordinal))).IsTrue();
        await Assert.That(lintResult.Diagnostics.Length).IsEqualTo(combinedResult.Diagnostics.Length);
    }

    [Test]
    public async Task ParseResult_ThenLint_ParserDiagnosticsPreserved()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ 1 + }}\n"u8.ToArray();
        var filePath = "contract.yml";

        using var parseResult = WorkflowParser.Parse(yaml, filePath);

        // Parser should find the syntax error
        await Assert.That(parseResult.Diagnostics.Any(d => d.Message.Contains("requires both operands", StringComparison.Ordinal) || d.Message.Contains("unexpected", StringComparison.Ordinal))).IsTrue();

        // Lint with pre-parsed result should preserve parser diagnostics
        using var lintResult = new LintEngine().Check(parseResult, yaml, filePath);
        await Assert.That(lintResult.Diagnostics.Any(d => d.Message.Contains("requires both operands", StringComparison.Ordinal) || d.Message.Contains("unexpected", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ParseResult_ThenLint_LinterAddsContextDiagnostics()
    {
        // steps context at job.if is linter-only
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: ${{ steps.foo.outputs.bar }}\n    steps:\n      - run: echo hi\n"u8.ToArray();
        var filePath = "contract.yml";

        using var parseResult = WorkflowParser.Parse(yaml, filePath);

        // Parser should NOT flag context availability
        await Assert.That(parseResult.Diagnostics.Any(d => d.Message.Contains("is not allowed here", StringComparison.Ordinal))).IsFalse();

        // Lint should add context availability diagnostic
        using var lintResult = new LintEngine().Check(parseResult, yaml, filePath);
        await Assert.That(lintResult.Diagnostics.Any(d => d.Message.Contains("is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ParserOnly_ReturnsWorkflowAst()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n"u8.ToArray();

        using var result = WorkflowParser.Parse(yaml, "api.yml");

        await Assert.That(result.Workflow).IsNotNull();
        await Assert.That(result.Workflow!.Jobs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LinterOnly_ReturnsAllDiagnostics()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ unknownFunc() }}\n"u8.ToArray();

        using var result = new LintEngine().Check(yaml, "api.yml");

        await Assert.That(result.Diagnostics.Length).IsGreaterThan(0);
        await Assert.That(result.Workflow).IsNotNull();
    }

    [Test]
    public async Task ParseResult_WithConfig_RespectsRuleSettings()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ unknownFunc() }}\n"u8.ToArray();

        using var parseResult = WorkflowParser.Parse(yaml, "api.yml");

        // Lint with default config
        using var lintResult = new LintEngine().Check(parseResult, yaml, "api.yml", config: null);
        await Assert.That(lintResult.Diagnostics.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task ParseResult_ThenLint_CanResolveStringsFromLintResult()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n"u8.ToArray();

        using var parseResult = WorkflowParser.Parse(yaml, "api.yml");
        using var lintResult = new LintEngine().Check(parseResult, yaml, "api.yml");

        // LintResult must be able to resolve strings through the borrowed arena
        await Assert.That(lintResult.Workflow).IsNotNull();
        var job = lintResult.Workflow!.Jobs.Values().First();
        var runsOn = lintResult.GetString(job.RunsOn!.Labels![0]);
        await Assert.That(runsOn).IsEqualTo("ubuntu-latest");
    }

    [Test]
    public async Task ParseResult_ThenLint_FatalActionMetadataParse_PreservesActionDocumentKind()
    {
        var yaml = "name: test\nruns: [\n"u8.ToArray();

        using var combinedResult = new LintEngine().Check(yaml, "action.yml");
        using var parseResult = WorkflowParser.Parse(yaml, "action.yml");
        using var lintResult = new LintEngine().Check(parseResult, yaml, "action.yml");

        await Assert.That(combinedResult.DocumentKind).IsEqualTo(DocumentKind.ActionMetadata);
        await Assert.That(lintResult.DocumentKind).IsEqualTo(combinedResult.DocumentKind);
    }

    [Test]
    public async Task ParseResult_ThenLint_WorkflowParse_PreservesWorkflowDocumentKind()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n"u8.ToArray();

        using var combinedResult = new LintEngine().Check(yaml, ".github/workflows/ci.yml");
        using var parseResult = WorkflowParser.Parse(yaml, ".github/workflows/ci.yml");
        using var lintResult = new LintEngine().Check(parseResult, yaml, ".github/workflows/ci.yml");

        await Assert.That(combinedResult.DocumentKind).IsEqualTo(DocumentKind.Workflow);
        await Assert.That(lintResult.DocumentKind).IsEqualTo(combinedResult.DocumentKind);
    }

    [Test]
    public async Task ParseResult_ThenLint_ActionMetadataParse_PreservesActionDocumentKind()
    {
        var yaml = "name: test\ndescription: test\nruns:\n  using: node20\n  main: index.js\n"u8.ToArray();

        using var combinedResult = new LintEngine().Check(yaml, "action.yml");
        using var parseResult = WorkflowParser.Parse(yaml, "action.yml");
        using var lintResult = new LintEngine().Check(parseResult, yaml, "action.yml");

        await Assert.That(combinedResult.DocumentKind).IsEqualTo(DocumentKind.ActionMetadata);
        await Assert.That(lintResult.DocumentKind).IsEqualTo(combinedResult.DocumentKind);
    }

    [Test]
    public async Task ParseResult_ThenLint_FatalParseWithoutHint_PreservesUnknownDocumentKind()
    {
        var yaml = "name: test\nruns: [\n"u8.ToArray();

        using var combinedResult = new LintEngine().Check(yaml, "broken.yml");
        using var parseResult = WorkflowParser.Parse(yaml, "broken.yml");
        using var lintResult = new LintEngine().Check(parseResult, yaml, "broken.yml");

        await Assert.That(combinedResult.DocumentKind).IsEqualTo(DocumentKind.Unknown);
        await Assert.That(lintResult.DocumentKind).IsEqualTo(combinedResult.DocumentKind);
    }

    [Test]
    public async Task ParseResult_ThenLint_DisposingLintResult_DoesNotDisposeParseResult()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n"u8.ToArray();

        using var parseResult = WorkflowParser.Parse(yaml, "api.yml");

        // Lint and immediately dispose the lint result
        var lintResult = new LintEngine().Check(parseResult, yaml, "api.yml");
        lintResult.Dispose();

        // ParseResult must still be usable (arena not disposed)
        await Assert.That(parseResult.Workflow).IsNotNull();
        var job = parseResult.Workflow!.Jobs.Values().First();
        var runsOn = parseResult.GetString(job.RunsOn!.Labels![0]);
        await Assert.That(runsOn).IsEqualTo("ubuntu-latest");
    }
}
