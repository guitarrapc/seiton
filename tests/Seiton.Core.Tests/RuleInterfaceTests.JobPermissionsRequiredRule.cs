using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_JobPermissionsRequiredRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-permissions-defined",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions:
                        contents: read
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-permissions-read-all",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: read-all
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-reusable-workflow-job-with-permissions",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    permissions:
                        contents: read
            """,
            []),
            new RuleCase(
            "ng-reusable-workflow-job-no-permissions",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
            """,
            ["does not have permissions defined"]),
            new RuleCase(
            "ng-no-permissions",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["does not have permissions defined"]),
            new RuleCase(
            "ng-multiple-jobs-one-missing",
            """
            on: push
            jobs:
                ok-job:
                    runs-on: ubuntu-latest
                    permissions:
                        contents: read
                    steps:
                        - run: echo ok
                ng-job:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["does not have permissions defined"]),
        };

        await AssertRuleCases(new JobPermissionsRequiredRule(), "job-permissions-required", cases);
    }
}
