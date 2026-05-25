using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_EnvVarRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-portable-env-keys",
            """
            on: push
            env:
                GLOBAL_TOKEN: x
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        JOB_TOKEN_1: x
                    steps:
                        - env:
                              STEP_TOKEN: x
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-env-key-lowercase",
            """
            on: push
            env:
                github_token: x
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["workflow.env key 'github_token' is not portable"]),
            new RuleCase(
            "ng-step-env-key-dash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                              TOKEN-NAME: x
                          run: echo ng
            """,
            ["step.env key 'TOKEN-NAME' is not portable"]),
        };

        await AssertRuleCases(new EnvVarRule(), "env-var", cases);
    }
}
