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
}
