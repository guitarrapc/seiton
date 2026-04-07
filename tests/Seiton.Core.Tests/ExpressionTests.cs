using System.Text;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class ExpressionTests
{
    [Test]
    public async Task Extract_FromWorkflowScalar_FindsExpressions()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: ${{ github.ref == 'refs/heads/main' }}\n    steps:\n      - run: echo ${{ matrix.os }}\n";

        var expressions = ExpressionExtractor.Extract(Encoding.UTF8.GetBytes(yaml));

        await Assert.That(expressions.Length).IsEqualTo(2);
        await Assert.That(expressions.Any(x => x.Expression.Contains("github.ref", StringComparison.Ordinal))).IsTrue();
        await Assert.That(expressions.Any(x => x.Expression.Contains("matrix.os", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_FunctionAndMemberAccess_Succeeds()
    {
        var expression = "startsWith(github.ref, 'refs/heads/main') && !cancelled()";

        var result = ExpressionParser.Parse(expression);

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Root).IsNotNull();
        await Assert.That(result.Root!.Kind).IsEqualTo(ExpressionSyntaxKind.Binary);
    }

    [Test]
    public async Task Parse_InvalidExpression_ReportsDiagnostics()
    {
        var result = ExpressionParser.Parse("github.");

        await Assert.That(result.Diagnostics.Length).IsGreaterThan(0);
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
