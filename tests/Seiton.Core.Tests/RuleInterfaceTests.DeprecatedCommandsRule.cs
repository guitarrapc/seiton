using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_DeprecatedCommandsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-modern-output-file",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "result=ok" >> "$GITHUB_OUTPUT"
            """,
            []),
            new RuleCase(
            "ng-set-output-command",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "::set-output name=result::ok"
            """,
            ["workflow command \"set-output\" was deprecated", "$GITHUB_OUTPUT"]),
            new RuleCase(
            "ng-set-env-command",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "::set-env name=TOKEN::x"
            """,
            ["workflow command \"set-env\" was deprecated", "$GITHUB_ENV"]),
            // regression: multi-line run script should report all deprecated commands
            new RuleCase(
            "ng-multiline-multiple-deprecated",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: |
                            echo "::set-output name=foo::bar"
                            echo "::set-env name=TOKEN::x"
            """,
            ["workflow command \"set-output\" was deprecated", "workflow command \"set-env\" was deprecated"]),
        };

        await AssertRuleCases(new DeprecatedCommandsRule(), "deprecated-commands", cases);
    }
}
