using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_MatrixRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-small-matrix",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                            node: [20]
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-empty-axis",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: []
                    steps:
                        - run: echo ng
            """,
            ["strategy.matrix axis 'os' has no values"]),
            new RuleCase(
            "ok-include-new-axis",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                            include:
                                - arch: x64
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-include-mixed-existing-and-new-axes",
            """
            on: push
            jobs:
                dispatch:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            repo: [guitarrapc/testtest]
                            include:
                                - repo: guitarrapc/testtest
                                  ref: main
                                  workflow: test
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-exclude-unknown-axis",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                            exclude:
                                - arch: x64
                    steps:
                        - run: echo ng
            """,
            ["strategy.matrix.exclude references unknown axis 'arch'"]),
        };

        await AssertRuleCases(new MatrixRule(), "matrix", cases);
    }


    [Test]
    public async Task RuleRegression_MatrixRule_DuplicateValues_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-duplicate-axis-value",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-20.04, ubuntu-22.04, ubuntu-20.04]
                    steps:
                        - run: echo ng
            """,
            ["duplicate"]),
            new RuleCase(
            "ok-unique-axis-values",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-20.04, ubuntu-22.04, ubuntu-24.04]
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new MatrixRule(), "matrix", cases);
    }


    [Test]
    public async Task RuleRegression_MatrixRule_ExcludeValueMismatch_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-scalar-value-mismatch",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            node: [10, 12, 14]
                            os: [ubuntu-latest, macos-latest]
                            exclude:
                                - node: 13
                                  os: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["does not match in matrix \"node\" combinations"]),
            new RuleCase(
            "ok-scalar-value-matches",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            node: [10, 12, 14]
                            os: [ubuntu-latest, macos-latest]
                            exclude:
                                - node: 10
                                  os: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-exclude-value-is-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            foo: [aaa]
                            exclude:
                                - foo: ${{ fromJSON('"x"') }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-row-value-is-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            foo:
                                - ${{ fromJSON('{"bar":"x"}') }}
                            exclude:
                                - foo: bar
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-include-only-axis-value-mismatch",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                            include:
                                - os: ubuntu-latest
                                  gui: gnome
                            exclude:
                                - os: ubuntu-latest
                                  gui: kde
                    steps:
                        - run: echo ng
            """,
            ["does not match in matrix \"gui\" combinations"]),
            new RuleCase(
            "ok-include-only-axis-value-matches",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                            include:
                                - os: ubuntu-latest
                                  gui: gnome
                            exclude:
                                - os: ubuntu-latest
                                  gui: gnome
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new MatrixRule(), "matrix", cases);
    }


    [Test]
    public async Task RuleRegression_MatrixRule_ExcludeObjectValueReportsAtValueLine()
    {
        // Object value in exclude: diagnostic must point to the exclude entry line, not the matrix range
        var yaml = """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.os.runner }}
                    strategy:
                        matrix:
                            os:
                                - {'runner': 'ubuntu-latest'}
                            exclude:
                                - os: {'runner': 'windows-latest'}
                    steps:
                        - run: echo ng
            """
            .Replace("\r\n", "\n");
        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "exclude-obj.yml");
        var diag = result.Diagnostics.FirstOrDefault(d => d.Message.Contains("does not match"));
        await Assert.That(diag.Message).IsNotNull();
        // The exclude entry is on line 10-11 area — diagnostic must not be on line 7 (matrix range)
        await Assert.That(diag.Location.StartLine).IsGreaterThanOrEqualTo(10);
    }


    [Test]
    public async Task RuleRegression_MatrixRule_ExcludeArrayValueReportsAtValueLine()
    {
        // Array value in exclude: diagnostic must point to the exclude entry line, not the matrix range
        var yaml = """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.os[0] }}
                    strategy:
                        matrix:
                            os:
                                - ['ubuntu', 'latest']
                            exclude:
                                - os: ['macos', 'latest']
                    steps:
                        - run: echo ng
            """
            .Replace("\r\n", "\n");
        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "exclude-arr.yml");
        var diag = result.Diagnostics.FirstOrDefault(d => d.Message.Contains("does not match"));
        await Assert.That(diag.Message).IsNotNull();
        // The exclude entry is on line 10-11 area — diagnostic must not be on line 7 (matrix range)
        await Assert.That(diag.Location.StartLine).IsGreaterThanOrEqualTo(10);
    }
}
