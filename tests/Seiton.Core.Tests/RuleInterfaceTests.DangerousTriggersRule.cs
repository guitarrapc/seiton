using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_DangerousTriggersRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-push",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-pull-request",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-pull-request-target",
            """
            on: pull_request_target
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["potentially dangerous"]),
            new RuleCase(
            "ng-workflow-run",
            """
            on: workflow_run
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["potentially dangerous"]),
            new RuleCase(
            "ng-multiple-dangerous-triggers",
            """
            on:
                pull_request_target:
                workflow_run:
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["potentially dangerous"]),
        };

        await AssertRuleCases(new DangerousTriggersRule(), "dangerous-triggers", cases);
    }
}
