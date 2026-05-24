using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

/// <summary>
/// Tests that verify the expression validation boundary between parser and linter.
/// These tests lock down which diagnostics are emitted by the parser alone vs the linter alone,
/// and verify that deduplication eliminates overlap correctly.
/// </summary>
public sealed class ExpressionBoundaryTests
{
    // --- Parser-only diagnostics: these must appear from parser without linter ---

    [Test]
    public async Task ParserOnly_SyntaxError_EmitsDiagnostic()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ github. }}\n"u8.ToArray();

        var result = WorkflowParser.ParseDirect(yaml, "boundary.yml", out var arena);

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("member name is missing", StringComparison.Ordinal))).IsTrue();
        arena?.Dispose();
    }

    [Test]
    public async Task ParserOnly_UnknownFunction_EmitsDiagnostic()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ unknownFunc() }}\n"u8.ToArray();

        var result = WorkflowParser.ParseDirect(yaml, "boundary.yml", out var arena);

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("unknown expression function", StringComparison.Ordinal))).IsTrue();
        arena?.Dispose();
    }

    [Test]
    public async Task ParserOnly_FunctionArityMismatch_EmitsDiagnostic()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ contains('a') }}\n"u8.ToArray();

        var result = WorkflowParser.ParseDirect(yaml, "boundary.yml", out var arena);

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("expects", StringComparison.Ordinal) && d.Message.Contains("argument", StringComparison.Ordinal))).IsTrue();
        arena?.Dispose();
    }

    [Test]
    public async Task ParserOnly_CompareNullLessThan_EmitsDiagnostic()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ null < 1 }}\n"u8.ToArray();

        var result = WorkflowParser.ParseDirect(yaml, "boundary.yml", out var arena);

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("cannot be compared", StringComparison.Ordinal))).IsTrue();
        arena?.Dispose();
    }

    [Test]
    public async Task ParserOnly_VarsGithubPrefix_EmitsDiagnostic()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ vars.GITHUB_TOKEN }}\n"u8.ToArray();

        var result = WorkflowParser.ParseDirect(yaml, "boundary.yml", out var arena);

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("GITHUB_", StringComparison.Ordinal))).IsTrue();
        arena?.Dispose();
    }

    // --- Linter-only diagnostics: these must NOT appear from parser alone ---

    [Test]
    public async Task ParserOnly_ContextAvailability_DoesNotEmitDiagnostic()
    {
        // steps context is not available at job.if position - but parser should NOT flag this
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: ${{ steps.foo.outputs.bar }}\n    steps:\n      - run: echo hi\n"u8.ToArray();

        var result = WorkflowParser.ParseDirect(yaml, "boundary.yml", out var arena);

        // Parser does not check context availability (this is linter-owned)
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("is not allowed here", StringComparison.Ordinal))).IsFalse();
        arena?.Dispose();
    }

    [Test]
    public async Task Lint_ContextAvailability_EmitsDiagnostic()
    {
        // steps context is not available at job.if position - linter MUST flag this
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: ${{ steps.foo.outputs.bar }}\n    steps:\n      - run: echo hi\n"u8.ToArray();

        using var result = new LintEngine().Check(yaml, "boundary.yml");

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ParserOnly_FunctionAvailability_DoesNotEmitDiagnostic()
    {
        // hashFiles is only available at step-level - parser should NOT flag this
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    env:\n      HASH: ${{ hashFiles('**/*.lock') }}\n    steps:\n      - run: echo hi\n"u8.ToArray();

        var result = WorkflowParser.ParseDirect(yaml, "boundary.yml", out var arena);

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("is not allowed here", StringComparison.Ordinal))).IsFalse();
        arena?.Dispose();
    }

    [Test]
    public async Task Lint_FunctionAvailability_EmitsDiagnostic()
    {
        // hashFiles at job.env position - linter MUST flag this
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    env:\n      HASH: ${{ hashFiles('**/*.lock') }}\n    steps:\n      - run: echo hi\n"u8.ToArray();

        using var result = new LintEngine().Check(yaml, "boundary.yml");

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ParserOnly_StatusFunctionOutsideIf_DoesNotEmitDiagnostic()
    {
        // success() outside if context - parser should NOT flag this
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ success() }}\n"u8.ToArray();

        var result = WorkflowParser.ParseDirect(yaml, "boundary.yml", out var arena);

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("is not allowed here", StringComparison.Ordinal))).IsFalse();
        arena?.Dispose();
    }

    [Test]
    public async Task Lint_StatusFunctionOutsideIf_EmitsDiagnostic()
    {
        // success() in step.run - linter MUST flag this
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ success() }}\n"u8.ToArray();

        using var result = new LintEngine().Check(yaml, "boundary.yml");

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    // --- Deduplication: lint result must not contain duplicate diagnostics ---

    [Test]
    public async Task Lint_ParserAndLinterOverlap_NoDuplicateDiagnostics()
    {
        // An expression with a syntax error - both parser and linter see it, but dedup should eliminate duplicates
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ github. }}\n"u8.ToArray();

        using var result = new LintEngine().Check(yaml, "boundary.yml");

        var messages = result.Diagnostics.Select(d => d.Message).ToArray();
        var distinct = messages.Distinct().ToArray();
        await Assert.That(messages.Length).IsEqualTo(distinct.Length);
    }

    [Test]
    public async Task Lint_DynamicPropertyOverlap_NoDuplicateDiagnostics()
    {
        // vars.GITHUB_FOO triggers parser diagnostic; linter may also check - no duplicates expected
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ vars.GITHUB_FOO }}\n"u8.ToArray();

        using var result = new LintEngine().Check(yaml, "boundary.yml");

        var messages = result.Diagnostics
            .Select(d => $"{d.Location.StartLine}:{d.Message}")
            .ToArray();
        var distinct = messages.Distinct().ToArray();
        await Assert.That(messages.Length).IsEqualTo(distinct.Length);
    }

    // --- Parser-intrinsic diagnostics must survive through lint ---

    [Test]
    public async Task Lint_UnknownFunction_PreservesParserDiagnostic()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ unknownFunc() }}\n"u8.ToArray();

        using var result = new LintEngine().Check(yaml, "boundary.yml");

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("unknown expression function", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Lint_SyntaxError_PreservesParserDiagnostic()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ 1 + }}\n"u8.ToArray();

        using var result = new LintEngine().Check(yaml, "boundary.yml");

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("requires both operands", StringComparison.Ordinal) || d.Message.Contains("unexpected", StringComparison.Ordinal))).IsTrue();
    }
}
