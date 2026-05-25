using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_DenyInheritSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-reusable-explicit-secrets",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    secrets:
                        token: ${{ secrets.GITHUB_TOKEN }}
            """,
            []),
            new RuleCase(
            "ok-normal-job-not-target",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-reusable-secrets-inherit",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    secrets: inherit
            """,
            ["uses 'secrets: inherit'", "explicitly map only required secrets"]),
        };

        await AssertRuleCases(new DenyInheritSecretsRule(), "deny-inherit-secrets", cases);
    }
}
