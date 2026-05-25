using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_GitHubAppTokenInputsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-non-target-action",
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
            "ok-actions-create-token-with-repositories-and-permission-prefix",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
                          with:
                              repositories: repo-a,repo-b
                              permission-contents: read
            """,
            []),
            new RuleCase(
            "ok-actions-create-token-current-repo-default-with-permission-prefix",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
                          with:
                              permission-contents: read
            """,
            []),
            new RuleCase(
            "ng-missing-permission-constraint-only-when-create-token-uses-current-repo-default",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
            """,
            ["permission constraints"]),
            new RuleCase(
            "ng-missing-repository-constraint-when-owner-set",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
                          with:
                              owner: ${{ github.repository_owner }}
                              permission-issues: write
            """,
            ["repository constraints"]),
            new RuleCase(
            "ng-missing-both-constraints-when-owner-set",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
                          with:
                              owner: ${{ github.repository_owner }}
            """,
            ["repository and permission constraints"]),
        };

        await AssertRuleCases(new GitHubAppTokenInputsRule(), "github-app-token-inputs", cases);
    }
}
