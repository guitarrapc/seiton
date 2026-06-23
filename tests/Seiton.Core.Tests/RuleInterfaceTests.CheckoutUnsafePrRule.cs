using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
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
            "ok-static-non-true-values",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v7
                          with:
                              allow-unsafe-pr-checkout: yes
                        - uses: actions/checkout@v7
                          with:
                              allow-unsafe-pr-checkout: 1
                        - uses: actions/checkout@v7
                          with:
                              allow-unsafe-pr-checkout: maybe
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
            "ng-uppercase-true",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v7
                          with:
                              allow-unsafe-pr-checkout: TRUE
            """,
            ["should not set with.allow-unsafe-pr-checkout to true"]),
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

    [Test]
    public async Task RuleRegression_CheckoutUnsafePrRule_Fix_RevalidatesLiteralValues()
    {
        var cases = new[]
        {
            new FixCase(
                "workflow-unquoted",
                "test.yaml",
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
                "allow-unsafe-pr-checkout: false"),
            new FixCase(
                "workflow-single-quoted",
                "test.yaml",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - uses: actions/checkout@v7
                              with:
                                  allow-unsafe-pr-checkout: 'true'
                """,
                "allow-unsafe-pr-checkout: 'false'"),
            new FixCase(
                "workflow-double-quoted",
                "test.yaml",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - uses: actions/checkout@v7
                              with:
                                  allow-unsafe-pr-checkout: "true"
                """,
                "allow-unsafe-pr-checkout: \"false\""),
            new FixCase(
                "action-metadata-unquoted",
                "action.yml",
                """
                name: unsafe checkout action
                description: test action
                runs:
                    using: composite
                    steps:
                        - uses: actions/checkout@v7
                          with:
                              allow-unsafe-pr-checkout: true
                """,
                "allow-unsafe-pr-checkout: false"),
        };

        foreach (var @case in cases)
        {
            var sourceBytes = Encoding.UTF8.GetBytes(@case.Yaml);
            var config = new LintConfig { Fix = new FixConfig { Enabled = true } };
            var engine = new LintEngine([new CheckoutUnsafePrRule()]);
            using var result = engine.Check(sourceBytes, @case.Path, config);
            var diagnostic = result.Diagnostics.First(d => d.RuleId == "checkout-unsafe-pr");

            using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, @case.Path, [diagnostic], config);
            var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

            await Assert.That(fixedText).Contains(@case.ExpectedReplacement);
            await Assert.That(revalidated.After.Diagnostics.Any(d => d.RuleId == "checkout-unsafe-pr")).IsFalse();
        }
    }

    [Test]
    public async Task RuleRegression_CheckoutUnsafePrRule_ActionMetadataCompositeSteps()
    {
        const string yaml = """
            name: unsafe checkout action
            description: test action
            inputs:
                allow_unsafe_pr_checkout:
                    description: test input
                    required: false
            runs:
                using: composite
                steps:
                    - uses: actions/checkout@v7
                      with:
                          allow-unsafe-pr-checkout: true
                    - uses: actions/checkout@v7
                      with:
                          allow-unsafe-pr-checkout: ${{ inputs.allow_unsafe_pr_checkout }}
                    - uses: actions/setup-node@v6
                      with:
                          allow-unsafe-pr-checkout: true
            """;

        using var result = new LintEngine([new CheckoutUnsafePrRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "action.yml");

        await Assert.That(result.DocumentKind).IsEqualTo(Parsing.DocumentKind.ActionMetadata);
        await Assert.That(result.HasFatalError).IsFalse();
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "checkout-unsafe-pr").ToArray();
        await Assert.That(diagnostics.Length).IsEqualTo(2);
        await Assert.That(diagnostics[0].Message).Contains("should not set with.allow-unsafe-pr-checkout to true");
    }

    private readonly record struct FixCase(string Name, string Path, string Yaml, string ExpectedReplacement);
}
