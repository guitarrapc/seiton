using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_DeprecatedPermissionsRule_TableDriven()
    {
        var cases = new[]
        {
            // no permissions at all — nothing to report
            new RuleCase(
            "ok-no-permissions",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // active scopes are never reported by this rule
            new RuleCase(
            "ok-active-scopes",
            """
            on: push
            jobs:
                build:
                    permissions:
                        contents: read
                        id-token: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // scalar form has no scope names to inspect
            new RuleCase(
            "ok-scalar-read-all",
            """
            on: push
            permissions: read-all
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // unknown scopes belong to the permissions rule, not this one
            new RuleCase(
            "ok-unknown-scope-not-reported",
            """
            on: push
            jobs:
                build:
                    permissions:
                        check: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // deprecated scope at job level
            new RuleCase(
            "ng-job-deprecated-models",
            """
            on: push
            jobs:
                build:
                    permissions:
                        contents: read
                        models: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permission scope \"models\" was deprecated"]),
            // deprecated scope at workflow level
            new RuleCase(
            "ng-workflow-deprecated-models",
            """
            on: push
            permissions:
                models: read
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permission scope \"models\" was deprecated"]),
            // deprecation is independent of the value being valid
            new RuleCase(
            "ng-deprecated-models-invalid-value",
            """
            on: push
            jobs:
                build:
                    permissions:
                        models: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permission scope \"models\" was deprecated"]),
        };

        await AssertRuleCases(new DeprecatedPermissionsRule(), "deprecated-permissions", cases);
    }
}
