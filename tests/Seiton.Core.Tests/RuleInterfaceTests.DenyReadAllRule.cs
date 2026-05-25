using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_DenyReadAllRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-workflow-explicit-scopes",
            """
            on: push
            permissions:
                contents: read
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-job-write-all-not-target",
            """
            on: push
            jobs:
                build:
                    permissions: write-all
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-read-all",
            """
            on: push
            permissions: read-all
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'read-all' is forbidden"]),
            new RuleCase(
            "ng-job-read-all",
            """
            on: push
            jobs:
                build:
                    permissions: read-all
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'read-all' is forbidden"]),
        };

        await AssertRuleCases(new DenyReadAllRule(), "deny-read-all", cases);
    }
}
