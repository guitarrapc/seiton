using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_PermissionsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-job-scope-read",
            """
            on: push
            jobs:
                build:
                    permissions:
                        contents: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-invalid-scalar",
            """
            on: push
            permissions: admin-all
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar must be 'read-all' or 'write-all'"]),
            new RuleCase(
            "ng-job-invalid-scope",
            """
            on: push
            jobs:
                build:
                    permissions:
                        contents: admin
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"admin\" is invalid as permission of scope \"contents\". available values are \"read\", \"write\", \"none\""]),
            // regression: unknown scope name should be detected
            new RuleCase(
            "ng-unknown-scope-check",
            """
            on: push
            jobs:
                test:
                    permissions:
                        check: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["unknown permission scope \"check\". all available permission scopes are"]),
            // regression: models scope is retired but still accepted by GitHub Actions, and only allows read/none
            new RuleCase(
            "ng-models-write-restricted",
            """
            on: push
            jobs:
                test:
                    permissions:
                        models: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"write\" is invalid as permission of scope \"models\". available values are \"read\", \"none\""]),
            // regression: id-token scope only allows write/none
            new RuleCase(
            "ng-id-token-read-restricted",
            """
            on: push
            jobs:
                test:
                    permissions:
                        id-token: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"read\" is invalid as permission of scope \"id-token\". available values are \"write\", \"none\""]),
            // regression: vulnerability-alerts only allows read/none
            new RuleCase(
            "ng-vulnerability-alerts-write-restricted",
            """
            on: push
            jobs:
                test:
                    permissions:
                        vulnerability-alerts: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"write\" is invalid as permission of scope \"vulnerability-alerts\". available values are \"read\", \"none\""]),
            // regression: valid scopes should not produce errors
            new RuleCase(
            "ok-all-standard-scopes-valid",
            """
            on: push
            jobs:
                test:
                    permissions:
                        actions: read
                        contents: write
                        issues: none
                        packages: read
                        id-token: write
                        models: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // regression: empty permissions scalar at job level (issue170)
            new RuleCase(
            "ng-job-empty-permissions-scalar",
            """
            on: push
            jobs:
                test:
                    permissions:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"\" is invalid for permission for all the scopes. available values are \"read-all\", \"write-all\" or {}"]),
            // regression: empty permissions scalar at workflow level (issue170)
            new RuleCase(
            "ng-workflow-empty-permissions-scalar",
            """
            on: push
            permissions:
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"\" is invalid for permission for all the scopes. available values are \"read-all\", \"write-all\" or {}"]),
            // warn: scalar read-all at workflow level
            new RuleCase(
            "ng-workflow-scalar-read-all",
            """
            on: push
            permissions: read-all
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'read-all' is overly broad; use explicit per-scope mapping in each job's permissions instead"]),
            // warn: scalar write-all at workflow level
            new RuleCase(
            "ng-workflow-scalar-write-all",
            """
            on: push
            permissions: write-all
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'write-all' is overly broad; use explicit per-scope mapping in each job's permissions instead"]),
            // warn: scalar read-all at job level
            new RuleCase(
            "ng-job-scalar-read-all",
            """
            on: push
            jobs:
                test:
                    permissions: read-all
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'read-all' is overly broad; use explicit per-scope mapping instead"]),
            // warn: scalar write-all at job level
            new RuleCase(
            "ng-job-scalar-write-all",
            """
            on: push
            jobs:
                test:
                    permissions: write-all
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'write-all' is overly broad; use explicit per-scope mapping instead"]),
        };

        await AssertRuleCases(new PermissionsRule(), "permissions", cases);
    }
}
