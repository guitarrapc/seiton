using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_IfCondRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-dynamic-condition",
            """
            on: push
            jobs:
                build:
                    if: ${{ github.ref != '' }}
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ success() }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-if-constant-false",
            """
            on: push
            jobs:
                build:
                    if: ${{ false }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["constant expression \"false\" in condition. remove the if: section"]),
            new RuleCase(
            "ng-step-if-constant-true",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ !false }}
                          run: echo ng
            """,
            ["constant expression \"!false\" in condition. remove the if: section"]),
            new RuleCase(
            "ng-step-if-always-true-multi-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ github.event_name == 'push' }} && ${{ github.ref_name == 'main' }}
                          run: echo ng
            """,
            ["always evaluated to true because extra characters are around"]),
            new RuleCase(
            "ng-step-if-always-true-trailing-space",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: "${{ github.event_name == 'push' }} "
                          run: echo ng
            """,
            ["always evaluated to true because extra characters are around"]),
            new RuleCase(
            "ok-step-if-bare-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.event_name == 'push'
                          run: echo ok
            """,
            []),
            // regression: null literal should be detected as constant (falsy)
            new RuleCase(
            "ng-step-if-null-literal",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ null }}
                          run: echo ng
            """,
            ["constant expression \"null\" in condition. remove the if: section"]),
            // regression: number literal should be detected as constant (0 = falsy)
            new RuleCase(
            "ng-step-if-number-zero",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ 0 }}
                          run: echo ng
            """,
            ["constant expression \"0\" in condition. remove the if: section"]),
            // regression: non-zero number is truthy
            new RuleCase(
            "ng-step-if-number-truthy",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ 42 }}
                          run: echo ng
            """,
            ["constant expression \"42\" in condition. remove the if: section"]),
            // regression: empty string literal is falsy
            new RuleCase(
            "ng-step-if-empty-string",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ '' }}
                          run: echo ng
            """,
            ["constant expression \"''\" in condition. remove the if: section"]),
            // regression: non-empty string literal is truthy
            new RuleCase(
            "ng-step-if-nonempty-string",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ 'hello' }}
                          run: echo ng
            """,
            ["constant expression \"'hello'\" in condition. remove the if: section"]),
            // regression: mixed type constant expression (true && 42 || !null)
            new RuleCase(
            "ng-step-if-mixed-constant",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: true && 42 || !null
                          run: echo ng
            """,
            ["constant expression \"true && 42 || !null\" in condition. remove the if: section"]),
            // regression: pure function with constant args (contains + format)
            new RuleCase(
            "ng-step-if-constant-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ contains(format('{0} {1} {2}', 'foo', 'bar', 'piyo'), 'o b') }}
                          run: echo ng
            """,
            ["constant expression"]),
            // ok case — impure function (success) should not be flagged
            new RuleCase(
            "ok-step-if-impure-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ contains(github.event.head_commit.message, 'skip') }}
                          run: echo ok
            """,
            []),
            // regression: trailing whitespace in bare constant should be trimmed in message text
            new RuleCase(
            "ng-step-if-constant-trailing-space",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: 'true '
                          run: echo ng
            """,
            ["constant expression \"true\" in condition. remove the if: section"]),
            // regression: leading whitespace in bare constant should be trimmed in message text
            new RuleCase(
            "ng-step-if-constant-leading-space",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ' false'
                          run: echo ng
            """,
            ["constant expression \"false\" in condition. remove the if: section"]),
            // regression: block scalar newline in constant should be trimmed in message text
            new RuleCase(
            "ng-step-if-constant-block-scalar",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - if: |\n          true\n        run: echo ng\n",
            ["constant expression \"true\" in condition. remove the if: section"]),
            // regression: snapshot.if constant should be detected
            new RuleCase(
            "ng-snapshot-if-constant",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    snapshot:
                        image-name: test
                        if: true
                    steps:
                        - run: echo ng
            """,
            ["constant expression \"true\" in condition. remove the if: section"]),
        };

        await AssertRuleCases(new IfCondRule(), "if-cond", cases);
    }





















    // Matrix duplicate value + exclude mismatch







    [Test]
    public async Task IfCondRule_BlockScalarConstant_ReportsAtIfKeyLine()
    {
        // MISS #7: block scalar `if: |\n  true` should report at the `if:` value line (where `|` is),
        // not at the content line (where `true` is).
        // Layout:
        //   line 6: "      - if: |"       <- `if` at col 9, `|` at col 13
        //   line 7: "          true"       <- content at col 11
        // actionlint expects line 6, col 13 (the `|` position)
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - if: |\n          true\n        run: echo ng\n";
        using var result = new LintEngine([new IfCondRule()]).Check(

            System.Text.Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).Contains("constant expression \"true\"");
        // Must report at block scalar indicator line, not content line
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(6);
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(13);
    }


    [Test]
    public async Task IfCondRule_BlockScalarAlwaysTrue_ReportsAtIfKeyLine()
    {
        // MISS #8: block scalar `if: |\n  ${{ false }}` should report at the `if:` value line,
        // not at the content line.
        // Layout:
        //   line 6: "      - if: |"              <- `|` at col 13
        //   line 7: "          ${{ false }}"      <- content at col 11
        // actionlint expects line 6, col 13
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - if: |\n          ${{ false }}\n        run: echo ng\n";
        using var result = new LintEngine([new IfCondRule()]).Check(

            System.Text.Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).Contains("always evaluated to true");
        // Must report at block scalar indicator line, not content line
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(6);
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(13);
    }


    [Test]
    public async Task IfCondRule_BlockScalarJobIf_ReportsAtIfKeyLine()
    {
        // Block scalar job-level `if: |\n  true` should also report at the `|` position.
        // Layout:
        //   line 4: "    if: |"      <- `if` at col 5, `|` at col 9
        //   line 5: "      true"     <- content at col 7
        var yaml = "on: push\njobs:\n  build:\n    if: |\n      true\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ng\n";
        using var result = new LintEngine([new IfCondRule()]).Check(

            System.Text.Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).Contains("constant expression \"true\"");
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(4);
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(9);
    }
}
