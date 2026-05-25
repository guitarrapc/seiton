using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_FakeTernaryRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-boolean-short-circuit",
            """
            on: push
            jobs:
                build:
                    if: ${{ (github.event_name == 'push' && success()) || failure() }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-if-fake-ternary",
            """
            on: push
            jobs:
                build:
                    if: ${{ github.ref_name == 'main' && 'prod' || 'dev' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["avoid fake ternary pattern 'cond && a || b'", "case expression"]),
            new RuleCase(
            "ng-step-if-fake-ternary",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.deploy && 'yes' || 'no' }}
                          run: echo ng
            """,
            ["avoid fake ternary pattern 'cond && a || b'", "explicit branching"]),
        };

        await AssertRuleCases(new FakeTernaryRule(), "fake-ternary", cases);
    }
}
