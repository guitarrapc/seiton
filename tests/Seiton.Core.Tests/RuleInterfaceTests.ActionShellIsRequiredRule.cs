using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_ActionShellIsRequiredRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
                        "ok-action-run-with-shell",
                        """
                        name: Sample action
                        runs:
                            using: composite
                            steps:
                                - run: echo hello
                                    shell: bash
                        """,
                        []),
                        new RuleCase(
                        "ok-run-with-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
                          shell: bash
            """,
            []),
            new RuleCase(
            "ok-action-step-no-run",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
            new RuleCase(
            "ok-workflow-run-without-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
            """,
            []),
            new RuleCase(
            "ok-workflow-run-with-empty-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
                          shell: ""
            """,
            []),
        };

        await AssertRuleCases(new ActionShellIsRequiredRule(), "action-shell-is-required", cases);
    }
}
