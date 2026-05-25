using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_RunnerNoLatestRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-ubuntu-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["moving latest label"]),
            new RuleCase(
            "ng-windows-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    steps:
                        - run: echo ng
            """,
            ["moving latest label"]),
            new RuleCase(
            "ng-macos-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: macos-latest
                    steps:
                        - run: echo ng
            """,
            ["moving latest label"]),
            new RuleCase(
            "ok-version-pinned-label",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-24.04
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-self-hosted-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: [self-hosted, linux, x64]
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-runs-on-expression-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new RunnerNoLatestRule(), "runner-no-latest", cases);
    }
}
