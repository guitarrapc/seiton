using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_SecretsOutsideEnvRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-secret-in-env-handoff",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        TOKEN: ${{ secrets.GITHUB_TOKEN }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-secret-in-step-if",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ secrets.GITHUB_TOKEN != '' }}
                          run: echo ng
            """,
            ["step.if", "secrets context"]),
            new RuleCase(
            "ok-secret-in-action-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/github-script@v7
                          with:
                              script: ${{ secrets.GITHUB_TOKEN }}
            """,
            []),
            new RuleCase(
            "ok-secret-in-create-github-app-token-inputs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
                          with:
                              app-id: ${{ secrets.APP_ID }}
                              private-key: ${{ secrets.PRIVATE_KEY }}
            """,
            []),
        };

        await AssertRuleCases(new SecretsOutsideEnvRule(), "secrets-outside-env", cases);
    }
}
