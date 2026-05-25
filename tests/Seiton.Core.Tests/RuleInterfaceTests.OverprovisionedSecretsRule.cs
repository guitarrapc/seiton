using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_OverprovisionedSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-single-secret-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            TOKEN: ${{ secrets.GITHUB_TOKEN }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-two-step-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            TOKEN: ${{ secrets.GITHUB_TOKEN }}
                            API_KEY: ${{ secrets.API_KEY }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-multiple-step-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            TOKEN: ${{ secrets.GITHUB_TOKEN }}
                            API_KEY: ${{ secrets.API_KEY }}
                            SECRET_KEY: ${{ secrets.SECRET_KEY }}
                            PRIVATE_KEY: ${{ secrets.PRIVATE_KEY }}
                            APP_ID: ${{ secrets.APP_ID }}
                            DEPLOY_KEY: ${{ secrets.DEPLOY_KEY }}
                          run: echo ng
            """,
            ["more than 5 secret values", "minimum required"]),
            new RuleCase(
            "ok-five-job-secrets",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@v1
                    secrets:
                        token: ${{ secrets.GITHUB_TOKEN }}
                        api_key: ${{ secrets.API_KEY }}
                        secret_key: ${{ secrets.SECRET_KEY }}
                        private_key: ${{ secrets.PRIVATE_KEY }}
                        app_id: ${{ secrets.APP_ID }}
            """,
            []),
            new RuleCase(
            "ng-reusable-call-many-secrets",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@v1
                    secrets:
                        token: ${{ secrets.GITHUB_TOKEN }}
                        api_key: ${{ secrets.API_KEY }}
                        secret_key: ${{ secrets.SECRET_KEY }}
                        private_key: ${{ secrets.PRIVATE_KEY }}
                        app_id: ${{ secrets.APP_ID }}
                        deploy_key: ${{ secrets.DEPLOY_KEY }}
            """,
            ["passes 6 explicit secrets", "minimum required secrets"]),
        };

        await AssertRuleCases(new OverprovisionedSecretsRule(), "overprovisioned-secrets", cases);
    }
}
