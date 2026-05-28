using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{
    /// <summary>
    /// Invariant: no <see cref="Diagnostic.Message"/> produced by any rule may contain
    /// a literal newline character. RuleBase.AddDiagnostic guarantees this by collapsing
    /// embedded newlines into a single space.
    /// </summary>
    [Test]
    public async Task DiagnosticMessage_NeverContainsNewline_BlockScalarInputs()
    {
        // Workflows that exercise block scalars in positions likely to embed into messages.
        var yamls = new[]
        {
            // Folded block scalar in step-level `if:`
            "on: push\njobs:\n  j:\n    runs-on: ubuntu-latest\n    steps:\n      - if: >\n          github.event_name == 'push' &&\n          github.ref == 'refs/heads/main'\n        run: echo ok\n",
            // Literal block scalar in job-level `if:`
            "on: push\njobs:\n  j:\n    if: |\n      needs.a.result != 'skipped' &&\n      needs.b.result != 'skipped'\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n",
            // Single-line folded
            "on: push\njobs:\n  j:\n    runs-on: ubuntu-latest\n    steps:\n      - if: >\n          github.event_name == 'push'\n        run: echo ok\n",
            // Short folded scalar (fold point within 32-byte anchor window)
            "on: push\njobs:\n  j:\n    if: >\n      a ||\n      b\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n",
        };

        var rules = new IRule[] { new IfExprWrapperRule() };
        foreach (var yaml in yamls)
        {
            using var result = new LintEngine(rules).Check(Encoding.UTF8.GetBytes(yaml), "invariant-test.yml");
            foreach (var d in result.Diagnostics)
            {
                await Assert.That(d.Message).DoesNotContain("\n");
                await Assert.That(d.Message).DoesNotContain("\r");
            }
        }
    }
}
