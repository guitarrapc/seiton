using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_IdNamingRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-valid-job-and-step-ids",
            """
            on: push
            jobs:
                Build_123:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: setup-1
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-no-step-id",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-step-id-expression-skipped",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: ${{ matrix.step_id }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-id-with-space",
            """
            on: push
            jobs:
                "bad id":
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid job ID", "must start with a letter"]),
            new RuleCase(
            "ng-step-id-with-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: setup.v1
                          run: echo ng
            """,
            ["invalid step ID", "must start with a letter"]),
            new RuleCase(
            "ng-step-id-empty",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: ''
                          run: echo ng
            """,
            ["step ID should not be empty"]),
            new RuleCase(
            "ng-job-id-starts-with-digit",
            """
            on: push
            jobs:
                1build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid job ID", "must start with a letter"]),
            new RuleCase(
            "ng-step-id-starts-with-dash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: -setup
                          run: echo ng
            """,
            ["invalid step ID", "must start with a letter"]),
            new RuleCase(
            "ng-step-id-duplicate-case-insensitive",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: BuildStep
                          run: echo first
                        - id: buildstep
                          run: echo second
            """,
            ["duplicated in the same job", "case-insensitive"]),
        };

        await AssertRuleCases(new IdNamingRule(), "id-naming", cases);
    }
}
