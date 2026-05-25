using System.Text;
using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_ExpressionParser_DoubleQuoteDetection()
    {
        // Double-quote in expression should produce a parse error suggesting single quotes
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                foo:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          continue-on-error: ${{ env.OS == "macos-latest" }}
            """);
        using var result = new LintEngine([]).Check(Encoding.UTF8.GetBytes(yaml), "issue193.yml");

        // Parser diagnostics have RuleId=null. Check all diagnostics.
        var hasDoubleQuoteError = result.Diagnostics.Any(x =>
            x.Message.Contains("'\"'", StringComparison.Ordinal) &&
            x.Message.Contains("single quote", StringComparison.OrdinalIgnoreCase));
        await Assert.That(hasDoubleQuoteError).IsTrue();
    }
}
