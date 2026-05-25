using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_ForbiddenUsesRule_TableDriven()
    {
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["forbidden-uses"] = new RuleConfig
                {
                    Allow = ["bad-org/safe-action"],
                    Deny = ["bad-org/*"],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-allowed-by-exception",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: bad-org/safe-action@v1
            """,
            []),
            new RuleCase(
            "ng-deny-policy-hit",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: bad-org/unsafe-action@v1
            """,
            ["denied by forbidden-uses policy", "bad-org/unsafe-action"]),
            new RuleCase(
            "ng-reusable-workflow-deny",
            """
            on: push
            jobs:
                reuse:
                    uses: bad-org/reusable/.github/workflows/reuse.yml@v1
            """,
            ["denied by forbidden-uses policy", "bad-org/reusable"]),
        };

        await AssertRuleCases(new ForbiddenUsesRule(), "forbidden-uses", cases, config);
    }
}
