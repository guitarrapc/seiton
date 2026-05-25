using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_InsecureCommandsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-unrelated-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        LOG_LEVEL: debug
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-env-unsecure-commands",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        ACTIONS_ALLOW_UNSECURE_COMMANDS: true
                    steps:
                        - run: echo ng
            """,
            ["ACTIONS_ALLOW_UNSECURE_COMMANDS", "migrate to environment files"]),
            new RuleCase(
            "ng-step-env-unsecure-commands",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            ACTIONS_ALLOW_UNSECURE_COMMANDS: "yes"
                          run: echo ng
            """,
            ["ACTIONS_ALLOW_UNSECURE_COMMANDS", "migrate to environment files"]),
        };

        await AssertRuleCases(new InsecureCommandsRule(), "insecure-commands", cases);
    }
}
