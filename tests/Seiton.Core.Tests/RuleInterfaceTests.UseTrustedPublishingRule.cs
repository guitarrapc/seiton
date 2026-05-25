using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_UseTrustedPublishingRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-publish-with-id-token-write",
            """
            on: push
            jobs:
                publish:
                    permissions:
                        id-token: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: npm publish
            """,
            []),
            new RuleCase(
            "ng-npm-publish-without-id-token",
            """
            on: push
            jobs:
                publish:
                    runs-on: ubuntu-latest
                    steps:
                        - run: npm publish
            """,
            ["publish-like command detected", "trusted publishing"]),
            new RuleCase(
            "ng-twine-upload-without-id-token",
            """
            on: push
            jobs:
                publish:
                    runs-on: ubuntu-latest
                    steps:
                        - run: twine upload dist/*
            """,
            ["publish-like command detected", "id-token: write"]),
        };

        await AssertRuleCases(new UseTrustedPublishingRule(), "use-trusted-publishing", cases);
    }
}
