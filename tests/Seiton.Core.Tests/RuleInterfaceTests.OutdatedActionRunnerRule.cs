using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_OutdatedActionRunnerRule_TableDriven()
    {
        // This rule is catalog-driven and version-aware: it checks the popular actions catalog
        // for deprecated runner versions. Actions with maxDeprecatedMajorVersion in the catalog
        // are flagged when the referenced version is at or below that threshold.
        var cases = new[]
        {
            new RuleCase(
            "ok-latest-version-node20",
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
            "ng-outdated-checkout-v3",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v3
            """,
            ["too old to run"]),
            new RuleCase(
            "ng-outdated-checkout-v2",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v2
            """,
            ["too old to run"]),
            new RuleCase(
            "ok-unknown-action-not-in-catalog",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: some/action@v1
            """,
            []),
            new RuleCase(
            "ok-sha-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@a5ac7e51b41094c92402da3b24376905380afc29
            """,
            []),
            new RuleCase(
            "ok-docker-login-current",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker/login-action@v3
            """,
            []),
            new RuleCase(
            "ng-docker-login-v2",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker/login-action@v2
            """,
            ["too old to run"]),
        };

        await AssertRuleCases(new OutdatedActionRunnerRule(), "outdated-action-runner", cases);
    }
}
