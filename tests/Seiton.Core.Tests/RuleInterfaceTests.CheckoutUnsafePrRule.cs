using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{
    [Test]
    public async Task RuleRegression_CheckoutUnsafePrRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-missing-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v7
            """,
            []),
            new RuleCase(
            "ok-false",
            """
            on: pull_request_target
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v7
                          with:
                              allow-unsafe-pr-checkout: false
            """,
            []),
            new RuleCase(
            "ok-uppercase-false",
            """
            on: workflow_run
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v7
                          with:
                              allow-unsafe-pr-checkout: FALSE
            """,
            []),
            new RuleCase(
            "ok-quoted-false",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v7
                          with:
                              allow-unsafe-pr-checkout: 'false'
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
                        - uses: actions/setup-node@v6
                          with:
                              allow-unsafe-pr-checkout: true
            """,
            []),
            new RuleCase(
            "ng-true",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v7
                          with:
                              allow-unsafe-pr-checkout: true
            """,
            ["should not set with.allow-unsafe-pr-checkout to true", "fork pull request code", "trusted context", "pwn request vulnerabilities"]),
            new RuleCase(
            "ng-quoted-true-old-version",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              allow-unsafe-pr-checkout: "true"
            """,
            ["should not set with.allow-unsafe-pr-checkout to true"]),
            new RuleCase(
            "ng-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v7
                          with:
                              allow-unsafe-pr-checkout: ${{ inputs.allow_unsafe_pr_checkout }}
            """,
            ["should not set with.allow-unsafe-pr-checkout to true"]),
        };

        await AssertRuleCases(new CheckoutUnsafePrRule(), "checkout-unsafe-pr", cases);
    }

    [Test]
    public async Task RuleRegression_CheckoutUnsafePrRule_Fix_LiteralTrueOnly()
    {
        const string literalYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v7
                          with:
                              allow-unsafe-pr-checkout: true
            """;
        const string expressionYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v7
                          with:
                              allow-unsafe-pr-checkout: ${{ inputs.allow_unsafe_pr_checkout }}
            """;

        using var literalResult = new LintEngine([new CheckoutUnsafePrRule()])
            .Check(Encoding.UTF8.GetBytes(literalYaml), "test.yaml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        using var expressionResult = new LintEngine([new CheckoutUnsafePrRule()])
            .Check(Encoding.UTF8.GetBytes(expressionYaml), "test.yaml", new LintConfig { Fix = new FixConfig { Enabled = true } });

        var literalDiagnostic = literalResult.Diagnostics.First(d => d.RuleId == "checkout-unsafe-pr");
        var expressionDiagnostic = expressionResult.Diagnostics.First(d => d.RuleId == "checkout-unsafe-pr");

        await Assert.That(literalDiagnostic.Fix is not null).IsTrue();
        await Assert.That(literalDiagnostic.Fix!.Value.Edits[0].NewText).IsEqualTo("false");
        await Assert.That(expressionDiagnostic.Fix is null).IsTrue();
    }
}
