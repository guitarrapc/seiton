using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_UnsoundConditionRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-plain-fenced-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: ${{ github.event_name == 'push' }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-strip-chomping-literal",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: |-\n      ${{ github.event_name == 'push' }}\n    steps:\n      - run: echo ok\n",
            []),
            new RuleCase(
            "ok-strip-chomping-folded",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: >-\n      ${{ github.event_name == 'push' }}\n    steps:\n      - run: echo ok\n",
            []),
            new RuleCase(
            "ok-no-expression",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: |\n      true\n    steps:\n      - run: echo ok\n",
            []),
            new RuleCase(
            "ng-literal-block-scalar-with-fenced-expr",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: |\n      ${{ github.event_name == 'push' }}\n    steps:\n      - run: echo ng\n",
            ["always truthy", "strip chomping"]),
            new RuleCase(
            "ng-folded-block-scalar-with-fenced-expr",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: >\n      ${{ github.event_name == 'push' }}\n    steps:\n      - run: echo ng\n",
            ["always truthy", "strip chomping"]),
            new RuleCase(
            "ng-step-block-scalar-with-fenced-expr",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - if: |\n          ${{ github.event_name == 'push' }}\n        run: echo ng\n",
            ["always truthy", "strip chomping"]),
        };

        await AssertRuleCases(new UnsoundConditionRule(), "unsound-condition", cases);
    }































































    [Test]
    public async Task UnsoundConditionRule_AutoFix_ReplacesLiteralIndicator()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: |\n      ${{ true }}\n    steps:\n      - run: echo test\n";
        var engine = new LintEngine([new UnsoundConditionRule()]);
        using var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var diag = result.Diagnostics.FirstOrDefault(d => d.RuleId == "unsound-condition");
        await Assert.That(diag.RuleId).IsEqualTo("unsound-condition");
        await Assert.That(diag.Fix is not null).IsTrue();
        await Assert.That(diag.Fix!.Value.Edits[0].NewText).IsEqualTo("|-");
    }
}
