using System.Text;
using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_FromJsonBrokenJson()
    {
        // fromJSON validation is done in the parser (not linter rule), so diagnostics have RuleId=null
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                foo:
                    strategy:
                        matrix:
                            include:
                                - invalid1: ${{ fromJSON('"foo') }}
                                - invalid2: ${{ fromJSON('["foo"') }}
                                - invalid3: ${{ fromJSON('') }}
                                - valid: ${{ fromJSON('"hello"') }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """);
        using var result = new LintEngine([]).Check(Encoding.UTF8.GetBytes(yaml), "fromjson-test.yml");
        var fromJsonErrors = result.Diagnostics
            .Where(x => x.Message.Contains("fromJSON()", StringComparison.Ordinal) && x.Message.Contains("JSON", StringComparison.Ordinal))
            .ToArray();

        // 3 broken JSON errors, none for valid JSON
        await Assert.That(fromJsonErrors).Count().IsEqualTo(3);
        await Assert.That(fromJsonErrors[0].Message).Contains("not valid JSON");
        await Assert.That(fromJsonErrors[1].Message).Contains("not valid JSON");
        await Assert.That(fromJsonErrors[2].Message).Contains("not valid JSON");
    }
}
