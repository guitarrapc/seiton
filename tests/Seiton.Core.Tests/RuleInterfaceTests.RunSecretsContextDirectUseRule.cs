using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_RunSecretsContextDirectUseRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-run-uses-shell-variable-only",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        TOKEN: ${{ secrets.MY_TOKEN }}
                    steps:
                        - run: echo "$TOKEN"
            """,
            []),
            new RuleCase(
            "ok-run-uses-non-secrets-expression",
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
            "ng-run-uses-secrets-dot-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ secrets.MY_TOKEN }}"
            """,
            ["must not reference", "secrets.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-secrets-bracket-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ secrets['MY_TOKEN'] }}"
            """,
            ["must not reference", "secrets.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-secrets-in-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ format('{0}', secrets.MY_TOKEN) }}"
            """,
            ["must not reference", "secrets.*", "shell variables"]),
        };

        await AssertRuleCases(new RunSecretsContextDirectUseRule(), "run-secrets-context-direct-use", cases);
    }
}
