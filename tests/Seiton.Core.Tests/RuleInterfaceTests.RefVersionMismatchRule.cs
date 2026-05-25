using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_RefVersionMismatchRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-matching-major",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: owner/action-v2@v2.1.0
            """,
            []),
            new RuleCase(
            "ng-repo-major-mismatch",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: owner/action-v1@v2.0.0
            """,
            ["major version 'v2' mismatches", "path version hint 'v1'"]),
            new RuleCase(
            "ng-workflow-path-major-mismatch",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/release-v1.yml@v3
            """,
            ["major version 'v3' mismatches", "path version hint 'v1'"]),
        };

        await AssertRuleCases(new RefVersionMismatchRule(), "ref-version-mismatch", cases);
    }
}
