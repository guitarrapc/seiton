using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_JobTimeoutMinutesRequiredRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-job-timeout-present",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    timeout-minutes: 15
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-step-timeout-on-all-steps",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - timeout-minutes: 3
                          run: echo ok
                        - timeout-minutes: 5
                          uses: actions/checkout@v4
            """,
            []),
            new RuleCase(
            "ok-reusable-workflow-call-not-target",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
            """,
            []),
            new RuleCase(
            "ng-missing-job-and-step-timeouts",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
                        - uses: actions/checkout@v4
            """,
            ["should define timeout-minutes", "default is 360 minutes", "set timeout-minutes on each step instead"]),
        };

        await AssertRuleCases(new JobTimeoutMinutesRequiredRule(), "job-timeout-minutes-required", cases);
    }
}
