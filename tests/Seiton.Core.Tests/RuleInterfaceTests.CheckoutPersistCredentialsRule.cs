using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_CheckoutPersistCredentialsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-checkout-persist-credentials-false",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
            """,
            []),
            new RuleCase(
            "ok-non-checkout-action",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/setup-node@v4
                          with:
                              persist-credentials: false
            """,
            []),
            new RuleCase(
            "ng-checkout-persist-credentials-missing",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            ["should set with.persist-credentials to false"]),
            new RuleCase(
            "ng-checkout-persist-credentials-true",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: true
            """,
            ["should set with.persist-credentials to false"]),
            new RuleCase(
            "ng-checkout-persist-credentials-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: ${{ inputs.persist_credentials }}
            """,
            ["should set with.persist-credentials to false"]),
            new RuleCase(
            "ok-checkout-persist-credentials-capitalized-False",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: False
            """,
            []),
            new RuleCase(
            "ok-checkout-persist-credentials-uppercase-FALSE",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: FALSE
            """,
            []),
        };

        await AssertRuleCases(new CheckoutPersistCredentialsRule(), "checkout-persist-credentials", cases);
    }

    [Test]
    public async Task RuleRegression_CheckoutPersistCredentials_Message_UsesConcreteAuthRecoveryExamples()
    {
        const string yaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """;

        using var result = new LintEngine([new CheckoutPersistCredentialsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "test.yaml");

        var diag = result.Diagnostics.First(d => d.RuleId == "checkout-persist-credentials");
        await Assert.That(diag.Message.Contains("git remote set-url origin <url>", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diag.Message.Contains("gh auth setup-git", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diag.Message.Contains("...", StringComparison.Ordinal)).IsFalse();
    }
}
