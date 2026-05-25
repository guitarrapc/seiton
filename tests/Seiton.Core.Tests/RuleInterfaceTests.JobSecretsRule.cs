using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_JobSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-single-step-job-exception",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        GITHUB_TOKEN: ${{ github.token }}
                    steps:
                        - run: echo only-step
            """,
            []),
            new RuleCase(
            "ok-multi-step-non-secret-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        NORMAL_VALUE: plain
                    steps:
                        - run: echo first
                        - run: echo second
            """,
            []),
            new RuleCase(
            "ng-multi-step-github-token-in-job-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        GITHUB_TOKEN: ${{ github.token }}
                    steps:
                        - run: echo first
                        - run: echo second
            """,
            ["must not set secrets.* or github.token", "step env"]),
            new RuleCase(
            "ng-multi-step-secrets-in-job-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        DATADOG_API_KEY: ${{ secrets.DATADOG_API_KEY }}
                    steps:
                        - run: echo first
                        - run: echo second
            """,
            ["must not set secrets.* or github.token", "DATADOG_API_KEY"]),
        };

        await AssertRuleCases(new JobSecretsRule(), "job-secrets", cases);
    }
}
