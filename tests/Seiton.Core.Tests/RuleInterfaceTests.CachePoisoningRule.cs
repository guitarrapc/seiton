using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_CachePoisoningRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-cache-on-trusted-trigger",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            []),
            new RuleCase(
            "ng-cache-on-pull-request",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            ["cache action", "untrusted triggers"]),
            new RuleCase(
            "ng-cache-restore-on-workflow-run",
            """
            on: workflow_run
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache/restore@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            ["cache action", "untrusted triggers"]),
        };

        await AssertRuleCases(new CachePoisoningRule(), "cache-poisoning-trigger", cases);
    }
}
