using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_RunEnvContextDirectUseRule_TableDriven()
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
                        VERSION: 1.2.3
                    steps:
                        - run: echo "$VERSION"
            """,
            []),
            new RuleCase(
            "ok-run-uses-non-env-expression",
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
            "ng-run-uses-env-dot-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        VERSION: 1.2.3
                    steps:
                        - run: echo "${{ env.VERSION }}"
            """,
            ["must not reference", "env.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-env-bracket-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        VERSION: 1.2.3
                    steps:
                        - run: echo "${{ env['VERSION'] }}"
            """,
            ["must not reference", "env.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-env-in-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        VERSION: 1.2.3
                    steps:
                        - run: echo "${{ format('{0}', env.VERSION) }}"
            """,
            ["must not reference", "env.*", "shell variables"]),
        };

        await AssertRuleCases(new RunEnvContextDirectUseRule(), "run-env-context-direct-use", cases);
    }
}
