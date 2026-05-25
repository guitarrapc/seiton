using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_WorkflowSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-single-job-workflow-exception",
            """
            on: push
            env:
                GITHUB_TOKEN: ${{ github.token }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-multi-job-non-secret-env",
            """
            on: push
            env:
                NORMAL_VALUE: plain
            jobs:
                a:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo a
                b:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo b
            """,
            []),
            new RuleCase(
            "ng-multi-job-github-token-in-workflow-env",
            """
            on: push
            env:
                GITHUB_TOKEN: ${{ github.token }}
            jobs:
                a:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo a
                b:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo b
            """,
            ["must not set secrets.* or github.token", "move secret mapping to job/step env"]),
            new RuleCase(
            "ng-multi-job-secrets-in-workflow-env",
            """
            on: push
            env:
                DATADOG_API_KEY: ${{ secrets.DATADOG_API_KEY }}
            jobs:
                a:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo a
                b:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo b
            """,
            ["must not set secrets.* or github.token", "DATADOG_API_KEY"]),
        };

        await AssertRuleCases(new WorkflowSecretsRule(), "workflow-secrets", cases);
    }
}
