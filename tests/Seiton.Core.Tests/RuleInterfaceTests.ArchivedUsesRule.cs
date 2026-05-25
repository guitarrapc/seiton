using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_ArchivedUsesRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-active-action",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
            new RuleCase(
            "ng-archived-action-repo",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions-rs/toolchain@v1
            """,
            ["is archived", "actions-rs/toolchain"]),
            new RuleCase(
            "ng-archived-reusable-workflow-repo",
            """
            on: push
            jobs:
                reuse:
                    uses: actions-rs/cargo/.github/workflows/reuse.yml@v1
            """,
            ["is archived", "actions-rs/cargo"]),
        };

        await AssertRuleCases(new ArchivedUsesRule(), "archived-uses", cases);
    }
}
