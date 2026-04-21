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
    public async Task Parse_MemberAccessIndexAccess_Succeeds()
    {
        // secrets[matrix.secret] — expression as index key (valid GitHub Actions syntax)
        var result = ExpressionParser.Parse("secrets[matrix.secret]"u8);

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.HasRoot).IsTrue();
        await Assert.That(result.Nodes[result.RootNode].Kind).IsEqualTo(ExpressionNodeKind.IndexAccess);
    }

    [Test]
    public async Task Parse_DeepMemberAccessIndexAccess_Succeeds()
    {
        // env[vars.key] — another common dynamic-key pattern
        var result = ExpressionParser.Parse("env[vars.key]"u8);

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.HasRoot).IsTrue();
    }

    [Test]
    public async Task ExtractAndParseFull_SecretsMatrixIndexAccess_NoParseDiagnostics()
    {
        // Full workflow round-trip: SECRET: ${{ secrets[matrix.secret] }} must not produce parse errors
        var yaml = """
            on: push
            jobs:
              build:
                permissions:
                  contents: read
                strategy:
                  matrix:
                    org: [apples]
                    include:
                      - org: apples
                        secret: APPLES
                runs-on: ubuntu-latest
                timeout-minutes: 1
                steps:
                  - run: echo $SECRET
                    env:
                      SECRET: ${{ secrets[matrix.secret] }}
            """;

        var result = ExpressionExtractor.ExtractAndParse(Encoding.UTF8.GetBytes(yaml));

        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("parse error", StringComparison.Ordinal))).IsFalse();
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
    public async Task ParseAndValidate_FunctionOverload_ArrayContains_AllowsArrayFirstArg()
    {
        var expression = "contains(fromJson('[1,2,3]'), 2)"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        // Regression: previously reported "expects 2 argument(s), but got 3" because
        // the inner fromJson call's argument was counted toward contains's ArgCount.
        await Assert.That(diagnostics.Any(x => x.Message.Contains("expects", StringComparison.Ordinal))).IsFalse();
        await Assert.That(diagnostics.Any(x => x.Message.Contains("argument 1 should be", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ParseAndValidate_FormatPlaceholderOutOfRange_ReportsDiagnostic()
    {
        var expression = "format('value-{1}', github.ref)"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        await Assert.That(diagnostics.Any(x => x.Message.Contains("format placeholder '{1}' requires argument 2, but got 1 format argument(s)", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ParseAndValidate_FormatPlaceholderInRange_DoesNotReportDiagnostic()
    {
        var expression = "format('value-{0}', github.ref)"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        await Assert.That(diagnostics.Any(x => x.Message.Contains("format placeholder", StringComparison.Ordinal))).IsFalse();
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

    [Test]
    public async Task InferType_FromJsonLiteralMemberAccess_ReturnsTypedProperty()
    {
        var expression = "fromJson('{\"enabled\":true}').enabled"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Bool);
    }

    [Test]
    public async Task InferType_FromJsonLiteralArrayIndex_ReturnsElementType()
    {
        var expression = "fromJson('[1,2,3]')[0]"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Number);
    }

    // ── InferType: context access ─────────────────────────────────────────────

    [Test]
    public async Task InferType_GitHubRef_ReturnsString()
    {
        var expression = "github.ref"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.String);
    }

    [Test]
    public async Task InferType_GitHubRefProtected_ReturnsBool()
    {
        var expression = "github.ref_protected"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Bool);
    }

    [Test]
    public async Task InferType_GitHubRetentionDays_ReturnsNumber()
    {
        var expression = "github.retention_days"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Number);
    }

    [Test]
    public async Task InferType_JobStatus_ReturnsString()
    {
        var expression = "job.status"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.String);
    }

    [Test]
    public async Task InferType_RunnerOs_ReturnsString()
    {
        var expression = "runner.os"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.String);
    }

    [Test]
    public async Task InferType_EnvVariable_ReturnsString()
    {
        var expression = "env.MY_VAR"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.String);
    }

    [Test]
    public async Task InferType_GitHubEventProperty_ReturnsAny()
    {
        var expression = "github.event.pull_request"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type).IsEqualTo(ExprType.Any);
    }

    [Test]
    public async Task InferType_GitHubContextRoot_ReturnsObject()
    {
        var expression = "github"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var type = ExpressionSemanticAnalyzer.InferType(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, expression);

        await Assert.That(type is ObjectExprType).IsTrue();
    }

    // ── Validate: context root and property checks ────────────────────────────

    [Test]
    public async Task ParseAndValidate_UndefinedRootContext_ReportsDiagnostic()
    {
        var expression = "goggle.actor"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        await Assert.That(diagnostics.Any(x => x.Message.Contains("undefined context 'goggle'", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ParseAndValidate_UnknownGithubProperty_ReportsDiagnostic()
    {
        var expression = "github.typo_field"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        await Assert.That(diagnostics.Any(x => x.Message.Contains("property 'typo_field' is not defined in 'github' object", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ParseAndValidate_ValidGithubProperty_NoDiagnostic()
    {
        var expression = "github.actor"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        await Assert.That(diagnostics.Any(x => x.Message.Contains("property", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ParseAndValidate_DynamicContextProperty_NoDiagnostic()
    {
        var expression = "env.MY_CUSTOM_VAR"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        await Assert.That(diagnostics.Any(x => x.Message.Contains("property", StringComparison.Ordinal))).IsFalse();
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

    // ── ExpressionVisitor.VisitExprNode ───────────────────────────────────────

    [Test]
    public async Task VisitExprNode_SingleLiteral_FiresEnterAndLeave()
    {
        var expression = "'hello'"u8;
        var parseResult = ExpressionParser.Parse(expression);
        var calls = new List<(bool entering, ExpressionNodeKind kind)>();

        ExpressionVisitor.VisitExprNode(
            parseResult.RootNode,
            parseResult.Nodes,
            parseResult.Arguments,
            (_, node, _, entering) => calls.Add((entering, node.Kind)));

        await Assert.That(calls.Count).IsEqualTo(2);
        await Assert.That(calls[0]).IsEqualTo((true, ExpressionNodeKind.StringLiteral));
        await Assert.That(calls[1]).IsEqualTo((false, ExpressionNodeKind.StringLiteral));
    }

    [Test]
    public async Task VisitExprNode_BinaryExpression_VisitsAllThreeNodes()
    {
        // `a && b` → Binary(And, Identifier(a), Identifier(b))
        var expression = "github.actor == 'x'"u8;
        var parseResult = ExpressionParser.Parse(expression);
        var visitedKinds = new List<ExpressionNodeKind>();

        ExpressionVisitor.VisitExprNode(
            parseResult.RootNode,
            parseResult.Nodes,
            parseResult.Arguments,
            (_, node, _, entering) =>
            {
                if (entering) visitedKinds.Add(node.Kind);
            });

        // Binary root, MemberAccess (github.actor), Identifier (github), StringLiteral ('x')
        await Assert.That(visitedKinds).Contains(ExpressionNodeKind.Binary);
        await Assert.That(visitedKinds).Contains(ExpressionNodeKind.MemberAccess);
        await Assert.That(visitedKinds).Contains(ExpressionNodeKind.Identifier);
        await Assert.That(visitedKinds).Contains(ExpressionNodeKind.StringLiteral);
    }

    [Test]
    public async Task VisitExprNode_FunctionCall_VisitsCalleeAndArguments()
    {
        var expression = "contains(github.ref, 'main')"u8;
        var parseResult = ExpressionParser.Parse(expression);
        var identifiers = CollectIdentifierNames(expression, parseResult);

        // "contains" (callee), "github" (context root in github.ref)
        await Assert.That(identifiers).Contains("contains");
        await Assert.That(identifiers).Contains("github");
    }

    /// <summary>
    /// Synchronous helper that uses the <see cref="IExprNodeVisitor"/> generic overload so that
    /// <see cref="ReadOnlySpan{byte}"/> is held directly in the <c>ref struct</c> visitor — no <c>ToArray()</c>.
    /// Must be a separate (non-async) method because <c>ref struct</c> locals cannot survive across <c>await</c>.
    /// </summary>
    private static List<string> CollectIdentifierNames(ReadOnlySpan<byte> expressionUtf8, ExpressionParseResult parseResult)
    {
        var visitor = new IdentifierNamesVisitor { ExpressionUtf8 = expressionUtf8, Identifiers = new List<string>() };
        ExpressionVisitor.VisitExprNode(parseResult.RootNode, parseResult.Nodes, parseResult.Arguments, ref visitor);
        return visitor.Identifiers;
    }

    /// <summary>
    /// Collects the string representations of all <see cref="ExpressionNodeKind.Identifier"/> nodes
    /// encountered during traversal. Declared as a <c>ref struct</c> implementing <see cref="IExprNodeVisitor"/>
    /// so it can hold <see cref="ReadOnlySpan{byte}"/> without any heap allocation.
    /// </summary>
    private ref struct IdentifierNamesVisitor : IExprNodeVisitor
    {
        public ReadOnlySpan<byte> ExpressionUtf8;
        public List<string> Identifiers;

        public void Visit(int nodeId, ExpressionNode node, int parentId, bool entering)
        {
            if (entering && node.Kind == ExpressionNodeKind.Identifier)
            {
                Identifiers.Add(Encoding.UTF8.GetString(node.Token.AsSpan(ExpressionUtf8)));
            }
        }
    }

    [Test]
    public async Task VisitExprNode_TraversalOrder_EnterBeforeLeave()
    {
        var expression = "!cancelled()"u8;
        var parseResult = ExpressionParser.Parse(expression);
        var log = new List<string>();

        ExpressionVisitor.VisitExprNode(
            parseResult.RootNode,
            parseResult.Nodes,
            parseResult.Arguments,
            (_, node, _, entering) => log.Add($"{(entering ? "enter" : "leave")}:{node.Kind}"));

        // Unary enter must precede its child's enter, and Unary leave must follow its child's leave.
        var unaryEnterIndex = log.IndexOf($"enter:{ExpressionNodeKind.Unary}");
        var unaryLeaveIndex = log.IndexOf($"leave:{ExpressionNodeKind.Unary}");
        var funcEnterIndex = log.IndexOf($"enter:{ExpressionNodeKind.FunctionCall}");
        var funcLeaveIndex = log.IndexOf($"leave:{ExpressionNodeKind.FunctionCall}");

        await Assert.That(unaryEnterIndex).IsLessThan(funcEnterIndex);
        await Assert.That(funcLeaveIndex).IsLessThan(unaryLeaveIndex);
    }

    [Test]
    public async Task VisitExprNode_ParentId_IsCorrectForChildren()
    {
        var expression = "github.ref"u8;
        var parseResult = ExpressionParser.Parse(expression);
        var parentMap = new Dictionary<int, int>(); // nodeId → parentId

        ExpressionVisitor.VisitExprNode(
            parseResult.RootNode,
            parseResult.Nodes,
            parseResult.Arguments,
            (nodeId, _, parentId, entering) =>
            {
                if (entering) parentMap[nodeId] = parentId;
            });

        // Root node must have parentId = -1.
        await Assert.That(parentMap[parseResult.RootNode]).IsEqualTo(-1);
    }

    [Test]
    public async Task ParseAndValidate_ContainsWithToJsonArg_NoDiagnostics()
    {
        // Regression: contains(toJSON(x), y) previously reported "expects 2 argument(s), but got 3"
        // because toJSON's inner argument was counted toward contains's ArgCount.
        var expression = "contains(toJSON(github.event.commits.*.message), '[build]')"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Job);

        await Assert.That(parseResult.Diagnostics).IsEmpty();
        await Assert.That(diagnostics.Any(x => x.Message.Contains("argument", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ParseAndValidate_ContainsWithNestedFunctionBothArgs_NoDiagnostics()
    {
        // f(g(x), h(y)) — both arguments are function calls; ArgStart must be correct for both.
        var expression = "contains(fromJson(github.event.inputs.ids), fromJson(github.event.inputs.item))"u8;
        var parseResult = ExpressionParser.Parse(expression);

        var diagnostics = ExpressionSemanticAnalyzer.Validate(
            parseResult,
            expression,
            new TextRange(0, expression.Length, 1, 1, 1, expression.Length),
            ExpressionValidationContext.Step);

        await Assert.That(parseResult.Diagnostics).IsEmpty();
        await Assert.That(diagnostics.Any(x => x.Message.Contains("expects", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ExtractAndParseFull_ContainsToJsonCommitMessages_NoParseDiagnostics()
    {
        // Full workflow round-trip for the contains(toJSON(commits.*.message), '[build]') pattern.
        var yaml = """
            name: trigger ci commit
            on:
              push:
                branches: ["main"]
            jobs:
              job:
                if: ${{ contains(toJSON(github.event.commits.*.message), '[build]') }}
                runs-on: ubuntu-24.04
                permissions:
                  contents: read
                timeout-minutes: 3
                steps:
                  - run: echo "$COMMIT_MESSAGES"
                    env:
                      COMMIT_MESSAGES: ${{ toJson(github.event.commits.*.message) }}
            """;

        var result = ExpressionExtractor.ExtractAndParse(Encoding.UTF8.GetBytes(yaml));

        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("parse error", StringComparison.Ordinal))).IsFalse();
    }
}
