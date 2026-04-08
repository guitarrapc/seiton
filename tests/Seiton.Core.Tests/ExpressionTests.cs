using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class ExpressionTests
{
    [Test]
    public async Task Extract_FromWorkflowScalar_FindsExpressions()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: ${{ github.ref == 'refs/heads/main' }}\n    steps:\n      - run: echo ${{ matrix.os }}\n";
        var bytes = Encoding.UTF8.GetBytes(yaml);

        var expressions = ExpressionExtractor.Extract(bytes);

        await Assert.That(expressions.Length).IsEqualTo(2);
        await Assert.That(expressions.Any(x => x.Slice.AsSpan(bytes).SequenceEqual("github.ref == 'refs/heads/main'"u8))).IsTrue();
        await Assert.That(expressions.Any(x => x.Slice.AsSpan(bytes).SequenceEqual("matrix.os"u8))).IsTrue();
    }

    [Test]
    public async Task Parse_FunctionAndMemberAccess_Succeeds()
    {
        var expression = "startsWith(github.ref, 'refs/heads/main') && !cancelled()"u8;

        var result = ExpressionParser.Parse(expression);

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.HasRoot).IsTrue();
        await Assert.That(result.Nodes[result.RootNode].Kind).IsEqualTo(ExpressionNodeKind.Binary);
    }

    [Test]
    public async Task Parse_InvalidExpression_ReportsDiagnostics()
    {
        var result = ExpressionParser.Parse("github."u8);

        await Assert.That(result.Diagnostics.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Parse_WildcardAndIndexAccess_Succeeds()
    {
        var result = ExpressionParser.Parse("github.event.pull_request.labels[*].name"u8);

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.HasRoot).IsTrue();
        await Assert.That(result.Nodes[result.RootNode].Kind).IsEqualTo(ExpressionNodeKind.MemberAccess);
    }

    [Test]
    public async Task Parse_StringIndexAccess_Succeeds()
    {
        var result = ExpressionParser.Parse("github.event['pull_request'].title"u8);

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.HasRoot).IsTrue();
    }

    [Test]
    public async Task ExtractAndParse_InvalidEmbeddedExpression_ReportsDiagnostics()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: ${{ github. }}\n    steps:\n      - run: echo ok\n";

        var result = ExpressionExtractor.ExtractAndParse(Encoding.UTF8.GetBytes(yaml));

        await Assert.That(result.Occurrences.Length).IsEqualTo(1);
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("expression parse error", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ExtractParseAndValidate_JobContext_DisallowsStepsContext()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: ${{ steps.build.outputs.value == 'ok' }}\n    steps:\n      - run: echo ok\n";

        var result = ExpressionExtractor.ExtractParseAndValidate(
            Encoding.UTF8.GetBytes(yaml),
            ExpressionValidationContext.Job);

        await Assert.That(result.Occurrences.Length).IsEqualTo(1);
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'steps' is not available in job expressions", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ParseAndValidate_UnknownFunction_ReportsDiagnostic()
    {
        var expression = "unknownFn(github.ref)"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        await Assert.That(diagnostics.Any(x => x.Message.Contains("unknown expression function: unknownFn", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ParseAndValidate_FunctionArity_ReportsDiagnostic()
    {
        var expression = "contains(github.ref)"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        await Assert.That(diagnostics.Any(x => x.Message.Contains("expects 2 argument(s), but got 1", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ParseAndValidate_FunctionTypeMismatch_ReportsDiagnostic()
    {
        var expression = "contains(1, 'x')"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        await Assert.That(diagnostics.Any(x => x.Message.Contains("argument 1 should be string, but got number", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ParseAndValidate_StepContext_AllowsStepsRoot()
    {
        var expression = "steps.build.outputs.value"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        await Assert.That(diagnostics.Any(x => x.Message.Contains("context 'steps' is not available", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_ArithmeticBinaryOperators_ReportsDiagnostics()
    {
        var result = ExpressionParser.Parse("1 + 2"u8);

        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unexpected token", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_UnaryMinus_ReportsDiagnostics()
    {
        var result = ExpressionParser.Parse("-1"u8);

        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unexpected token '-'", StringComparison.Ordinal))).IsTrue();
    }

    // ── InferType: literal nodes ──────────────────────────────────────────────

    [Test]
    public async Task InferType_StringLiteral_ReturnsString()
    {
        var expression = "'hello'"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.String);
    }

    [Test]
    public async Task InferType_NumberLiteral_ReturnsNumber()
    {
        var expression = "42"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Number);
    }

    [Test]
    public async Task InferType_BooleanLiteral_ReturnsBool()
    {
        var expression = "true"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Bool);
    }

    [Test]
    public async Task InferType_NullLiteral_ReturnsNull()
    {
        var expression = "null"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Null);
    }

    // ── InferType: unary and binary operators ─────────────────────────────────

    [Test]
    public async Task InferType_UnaryNot_ReturnsBool()
    {
        var expression = "!cancelled()"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Bool);
    }

    [Test]
    public async Task InferType_BinaryComparison_ReturnsBool()
    {
        var expression = "github.ref == 'refs/heads/main'"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Bool);
    }

    [Test]
    public async Task InferType_BinaryLogical_ReturnsBool()
    {
        var expression = "success() && github.event_name == 'push'"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Bool);
    }

    // ── InferType: function return types ──────────────────────────────────────

    [Test]
    public async Task InferType_BoolReturningFunction_ReturnsBool()
    {
        var expression = "contains(github.ref, 'main')"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Bool);
    }

    [Test]
    public async Task InferType_StringReturningFunction_ReturnsString()
    {
        var expression = "format('Hello {0}', github.actor)"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.String);
    }

    [Test]
    public async Task InferType_AnyReturningFunction_ReturnsAny()
    {
        var expression = "fromJson(steps.build.outputs.matrix)"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Any);
    }

    // ── InferType: context access ─────────────────────────────────────────────

    [Test]
    public async Task InferType_ContextAccess_ReturnsAny()
    {
        var expression = "github.ref"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Any);
    }

    // ── ValidateStringArg: improved bottom-up type check ─────────────────────

    [Test]
    public async Task ParseAndValidate_BinaryExprPassedAsStringArg_ReportsDiagnostic()
    {
        var expression = "contains(github.sha == 'abc', 'x')"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        await Assert.That(diagnostics.Any(x => x.Message.Contains("argument 1 should be string, but got bool", StringComparison.Ordinal))).IsTrue();
    }
}
