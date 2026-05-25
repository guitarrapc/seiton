using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_SecretsWholeContextAccessRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-specific-key-in-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        MY_SECRET: ${{ secrets.MY_TOKEN }}
                    steps:
                        - run: echo "$MY_SECRET"
            """,
            []),
            new RuleCase(
            "ok-no-secrets-reference",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref_name }}"
            """,
            []),
            new RuleCase(
            "ng-run-tojson-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ toJson(secrets) }}"
            """,
            ["must not reference", "secrets", "context object"]),
            new RuleCase(
            "ng-step-env-tojson-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/some-action@v1
                          env:
                            ALL_SECRETS: ${{ toJson(secrets) }}
            """,
            ["must not reference", "secrets", "context object"]),
            new RuleCase(
            "ng-step-with-tojson-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/some-action@v1
                          with:
                            all-secrets: ${{ toJson(secrets) }}
            """,
            ["must not reference", "secrets", "context object"]),
            new RuleCase(
            "ng-job-env-tojson-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        ALL_SECRETS: ${{ toJson(secrets) }}
                    steps:
                        - run: echo ok
            """,
            ["must not reference", "secrets", "context object"]),
            new RuleCase(
            "ng-format-function-whole-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ format('{0}', secrets) }}"
            """,
            ["must not reference", "secrets", "context object"]),
        };

        await AssertRuleCases(new SecretsWholeContextAccessRule(), "secrets-whole-context-access", cases);
    }
}
