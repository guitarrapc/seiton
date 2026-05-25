using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_PopularActionInputsRule_TypoSuggestion()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-typo-underscore-for-hyphen",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/setup-node@v4
                          with: { node_version: '20' }
            """,
            ["unknown input 'node_version' for action 'actions/setup-node@v4'. available inputs are", "did you mean 'node-version'?"]),
            new RuleCase(
            "ng-typo-close-misspelling",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with: { fetch-depht: 1 }
            """,
            ["unknown input 'fetch-depht' for action 'actions/checkout@v4'. available inputs are", "did you mean 'fetch-depth'?"]),
            new RuleCase(
            "ng-no-suggestion-for-distant-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with: { totally-unknown-input: true }
            """,
            ["unknown input 'totally-unknown-input' for action 'actions/checkout@v4'. available inputs are"]),
        };

        await AssertRuleCases(new PopularActionInputsRule(), "popular-action-inputs", cases);
    }


    [Test]
    public async Task RuleRegression_PopularActionInputsRule_TypoAutoFix()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                          fetch-depht: 1
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new PopularActionInputsRule()]);
        using var result = engine.Check(sourceBytes, "popular-action-inputs-fix.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x =>
            x.RuleId == "popular-action-inputs" && x.Message.Contains("fetch-depht", StringComparison.Ordinal));

        await Assert.That(diagnostic.Fix is not null).IsTrue();
        await Assert.That(diagnostic.Fix!.Value.Description).Contains("fetch-depth");

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "popular-action-inputs-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(fixedText).Contains("fetch-depth: 1");
        await Assert.That(revalidated.After.Diagnostics.Any(x =>
            x.RuleId == "popular-action-inputs" && x.Message.Contains("unknown input", StringComparison.Ordinal))).IsFalse();
    }


    [Test]
    public async Task RuleRegression_PopularActionInputsRule_NoFixWhenDistant()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                          totally-unknown-input: true
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new PopularActionInputsRule()]);
        using var result = engine.Check(sourceBytes, "popular-action-inputs-no-fix.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x =>
            x.RuleId == "popular-action-inputs" && x.Message.Contains("totally-unknown-input", StringComparison.Ordinal));

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }


    [Test]
    public async Task RuleRegression_PopularActionInputsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-known-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with: { fetch-depth: 1 }
            """,
            []),
            new RuleCase(
            "ng-typo-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with: { fetch-depht: 1 }
            """,
            ["unknown input 'fetch-depht' for action 'actions/checkout@v4'. available inputs are"]),
            new RuleCase(
            "ng-unknown-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with: { totally-unknown-input: true }
            """,
            ["unknown input 'totally-unknown-input' for action 'actions/checkout@v4'. available inputs are"]),
        };

        await AssertRuleCases(new PopularActionInputsRule(), "popular-action-inputs", cases);
    }


    [Test]
    public async Task RuleRegression_PopularActionInputsRule_RequiredInputs_TableDriven()
    {
        var cases = new[]
        {
            // #10: actions/cache requires 'path' and 'key' — missing both should warn
            new RuleCase(
            "ng-cache-missing-required-inputs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                            restore-keys: |
                                some-key-
            """,
            ["missing required input 'key' for action 'actions/cache@v4'", "missing required input 'path' for action 'actions/cache@v4'"]),
            // #10: actions/cache with required inputs present — no error
            new RuleCase(
            "ok-cache-all-required-inputs-present",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                            path: ~/.npm
                            key: npm-${{ runner.os }}
            """,
            []),
            // #10: actions/checkout has no required inputs without defaults — no error even with empty with
            new RuleCase(
            "ok-checkout-no-required-inputs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
        };

        await AssertRuleCases(new PopularActionInputsRule(), "popular-action-inputs", cases);
    }


    [Test]
    public async Task RuleRegression_PopularActionInputsRule_DeprecatedInputs_TableDriven()
    {
        var cases = new[]
        {
            // Deprecated input for reviewdog/action-actionlint
            new RuleCase(
            "ng-deprecated-fail-on-error",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: reviewdog/action-actionlint@v1
                          with:
                            fail_on_error: true
            """,
            ["avoid using deprecated input \"fail_on_error\" in action \"reviewdog/action-actionlint@v1\": Deprecated, use `fail_level` instead"]),
            // Deprecated inputs for pypa/gh-action-pypi-publish
            new RuleCase(
            "ng-deprecated-pypa-packages-dir",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: pypa/gh-action-pypi-publish@release/v1
                          with:
                            packages_dir: /path/to/dir
                            repository_url: https://github.com/foo/bar
            """,
            [
                "avoid using deprecated input \"packages_dir\" in action \"pypa/gh-action-pypi-publish@release/v1\": The inputs have been normalized to use kebab-case. Use `packages-dir` instead",
                "avoid using deprecated input \"repository_url\" in action \"pypa/gh-action-pypi-publish@release/v1\": The inputs have been normalized to use kebab-case. Use `repository-url` instead",
            ]),
            // Non-deprecated input should not trigger warning
            new RuleCase(
            "ok-non-deprecated-input",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                            path: ~/.npm
                            key: npm-${{ runner.os }}
            """,
            []),
        };

        await AssertRuleCases(new PopularActionInputsRule(), "popular-action-inputs", cases);
    }
}
