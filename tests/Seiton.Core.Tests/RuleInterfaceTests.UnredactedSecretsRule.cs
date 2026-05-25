using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_UnredactedSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-non-secret-env-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        VERSION: 1.2.3
                    steps:
                        - run: echo "${VERSION}"
            """,
            []),
            new RuleCase(
            "ng-secret-derived-env-echo",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        TOKEN: ${{ secrets.GITHUB_TOKEN }}
                    steps:
                        - run: echo "${TOKEN}"
            """,
            ["secret-derived variable", "without masking"]),
            new RuleCase(
            "ng-secret-derived-env-write-host",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    env:
                        TOKEN: ${{ secrets.GITHUB_TOKEN }}
                    steps:
                        - shell: pwsh
                          run: Write-Host "$env:TOKEN"
            """,
            ["secret-derived variable", "without masking"]),
        };

        await AssertRuleCases(new UnredactedSecretsRule(), "unredacted-secrets", cases);
    }
}
