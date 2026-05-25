using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_SelfHostedRunnerRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-self-hosted-on-push",
            """
            on: push
            jobs:
                build:
                    runs-on: self-hosted
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-self-hosted-on-pull-request",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: self-hosted
                    steps:
                        - run: echo ok
            """,
            ["self-hosted runner", "untrusted triggers"]),
            new RuleCase(
            "ng-self-hosted-on-workflow-run",
            """
            on: workflow_run
            jobs:
                build:
                    runs-on:
                        - self-hosted
                        - linux
                    steps:
                        - run: echo ok
            """,
            ["self-hosted runner", "untrusted triggers"]),
            new RuleCase(
            "ng-self-hosted-message-has-runs-on-path",
            """
            on: pull_request
            jobs:
                ci:
                    runs-on: self-hosted
                    steps:
                        - run: echo ok
            """,
            ["jobs.'ci'.runs-on"]),
        };

        await AssertRuleCases(new SelfHostedRunnerRule(), "self-hosted-runner", cases);
    }
}
