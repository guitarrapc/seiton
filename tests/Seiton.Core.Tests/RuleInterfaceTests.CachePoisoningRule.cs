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
            "ok-cache-on-pull-request",
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
            []),
            new RuleCase(
            "ok-cache-on-push-and-pull-request",
            """
            on:
                push:
                    branches: [main]
                pull_request:
                    branches: [main]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                              path: ~/.cache/go-build
                              key: ${{ runner.os }}-go-${{ hashFiles('**/go.sum') }}
            """,
            []),
            new RuleCase(
            "ok-cache-restore-on-pull-request-target",
            """
            on: pull_request_target
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache/restore@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            []),
            new RuleCase(
            "ok-cache-restore-on-workflow-run",
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
            []),
            new RuleCase(
            "ok-cache-restore-on-issue-comment",
            """
            on: issue_comment
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache/restore@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            []),
            new RuleCase(
            "ng-cache-lookup-only-on-pull-request-target",
            """
            on: pull_request_target
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
                              lookup-only: true
            """,
            ["write-capable cache action", "low-trust triggers"]),
            new RuleCase(
            "ng-cache-on-push-and-pull-request-target",
            """
            on:
                push:
                    branches: [main]
                pull_request_target:
                    types: [labeled]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            ["write-capable cache action", "low-trust triggers"]),
            new RuleCase(
            "ng-cache-on-pull-request-target",
            """
            on: pull_request_target
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            ["write-capable cache action", "low-trust triggers"]),
            new RuleCase(
            "ng-cache-save-on-issue-comment",
            """
            on: issue_comment
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache/save@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            ["write-capable cache action", "low-trust triggers"]),
            new RuleCase(
            "ng-cache-on-workflow-run",
            """
            on: workflow_run
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            ["write-capable cache action", "low-trust triggers"]),
        };

        await AssertRuleCases(new CachePoisoningRule(), "cache-poisoning-trigger", cases);
    }
}
