using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{
    [Test]
    public async Task RuleRegression_JobStructureRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-normal-job",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-uses-with-steps",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    steps:
                        - run: echo ng
            """,
            ["cannot have both uses and steps"]),
            new RuleCase(
            "ng-missing-runs-on",
            """
            on: push
            jobs:
                build:
                    steps:
                        - run: echo ng
            """,
            ["\"runs-on\" section is missing"]),
            new RuleCase(
            "ok-empty-uses-key-suppresses-runs-on-and-steps",
            """
            on: push
            jobs:
                call4:
                    uses:
                normal:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new JobStructureRule(), "job-structure", cases);
    }

    [Test]
    public async Task RuleRegression_ReusableWorkflowRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-reusable-allowed-keys",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    with:
                        target: prod
                    secrets: inherit
                    if: ${{ github.ref != '' }}
                    needs: []
                    concurrency: deploy
            """,
            []),
            new RuleCase(
            "ng-without-uses",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    with:
                        target: prod
                    steps:
                        - run: echo ng
            """,
            ["key 'with' requires uses"]),
            new RuleCase(
            "ng-forbidden-key-with-uses",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    container: node:20
            """,
            ["calls reusable workflow with uses"]),
            new RuleCase(
            "ng-remote-missing-ref",
            """
            on: push
            jobs:
                reuse:
                    uses: "foo/bar/workflow.yml"
            """,
            ["is not following the format"]),
            new RuleCase(
            "ng-remote-absolute-path",
            """
            on: push
            jobs:
                reuse:
                    uses: "/foo/bar/workflow.yml@main"
            """,
            ["is not following the format"]),
            new RuleCase(
            "ng-remote-missing-repo-path",
            """
            on: push
            jobs:
                reuse:
                    uses: "foo/workflow.yml@main"
            """,
            ["is not following the format"]),
            new RuleCase(
            "ok-remote-valid-format",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/path/to/workflow.yml@main
            """,
            []),
        };

        await AssertRuleCases(new ReusableWorkflowRule(), "reusable-workflow", cases);
    }

    [Test]
    public async Task RuleRegression_DenyInheritSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-reusable-explicit-secrets",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    secrets:
                        token: ${{ secrets.GITHUB_TOKEN }}
            """,
            []),
            new RuleCase(
            "ok-normal-job-not-target",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-reusable-secrets-inherit",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    secrets: inherit
            """,
            ["uses 'secrets: inherit'", "explicitly map only required secrets"]),
        };

        await AssertRuleCases(new DenyInheritSecretsRule(), "deny-inherit-secrets", cases);
    }

    [Test]
    public async Task RuleRegression_PermissionsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-job-scope-read",
            """
            on: push
            jobs:
                build:
                    permissions:
                        contents: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-invalid-scalar",
            """
            on: push
            permissions: admin-all
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar must be 'read-all' or 'write-all'"]),
            new RuleCase(
            "ng-job-invalid-scope",
            """
            on: push
            jobs:
                build:
                    permissions:
                        contents: admin
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"admin\" is invalid as permission of scope \"contents\". available values are \"read\", \"write\", \"none\""]),
            // regression: unknown scope name should be detected
            new RuleCase(
            "ng-unknown-scope-check",
            """
            on: push
            jobs:
                test:
                    permissions:
                        check: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["unknown permission scope \"check\". all available permission scopes are"]),
            // regression: models scope only allows read/none
            new RuleCase(
            "ng-models-write-restricted",
            """
            on: push
            jobs:
                test:
                    permissions:
                        models: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"write\" is invalid as permission of scope \"models\". available values are \"read\", \"none\""]),
            // regression: id-token scope only allows write/none
            new RuleCase(
            "ng-id-token-read-restricted",
            """
            on: push
            jobs:
                test:
                    permissions:
                        id-token: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"read\" is invalid as permission of scope \"id-token\". available values are \"write\", \"none\""]),
            // regression: vulnerability-alerts only allows read/none
            new RuleCase(
            "ng-vulnerability-alerts-write-restricted",
            """
            on: push
            jobs:
                test:
                    permissions:
                        vulnerability-alerts: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"write\" is invalid as permission of scope \"vulnerability-alerts\". available values are \"read\", \"none\""]),
            // regression: valid scopes should not produce errors
            new RuleCase(
            "ok-all-standard-scopes-valid",
            """
            on: push
            jobs:
                test:
                    permissions:
                        actions: read
                        contents: write
                        issues: none
                        packages: read
                        id-token: write
                        models: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // regression: empty permissions scalar at job level (issue170)
            new RuleCase(
            "ng-job-empty-permissions-scalar",
            """
            on: push
            jobs:
                test:
                    permissions:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"\" is invalid for permission for all the scopes. available values are \"read-all\", \"write-all\" or {}"]),
            // regression: empty permissions scalar at workflow level (issue170)
            new RuleCase(
            "ng-workflow-empty-permissions-scalar",
            """
            on: push
            permissions:
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["\"\" is invalid for permission for all the scopes. available values are \"read-all\", \"write-all\" or {}"]),
            // warn: scalar read-all at workflow level
            new RuleCase(
            "ng-workflow-scalar-read-all",
            """
            on: push
            permissions: read-all
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'read-all' is overly broad; use explicit per-scope mapping in each job's permissions instead"]),
            // warn: scalar write-all at workflow level
            new RuleCase(
            "ng-workflow-scalar-write-all",
            """
            on: push
            permissions: write-all
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'write-all' is overly broad; use explicit per-scope mapping in each job's permissions instead"]),
            // warn: scalar read-all at job level
            new RuleCase(
            "ng-job-scalar-read-all",
            """
            on: push
            jobs:
                test:
                    permissions: read-all
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'read-all' is overly broad; use explicit per-scope mapping instead"]),
            // warn: scalar write-all at job level
            new RuleCase(
            "ng-job-scalar-write-all",
            """
            on: push
            jobs:
                test:
                    permissions: write-all
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'write-all' is overly broad; use explicit per-scope mapping instead"]),
        };

        await AssertRuleCases(new PermissionsRule(), "permissions", cases);
    }

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

        var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "popular-action-inputs-fix.yml", [diagnostic]);
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
    public async Task RuleRegression_UnpinnedUsesRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-action-pinned-sha",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@0123456789abcdef0123456789abcdef01234567
            """,
            []),
            new RuleCase(
            "ng-action-tag-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            ["not pinned to a full-length commit SHA"]),
            new RuleCase(
            "ng-action-missing-ref-format",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout
            """,
            ["invalid reference format", "owner/repo[/path]@ref"]),
            new RuleCase(
            "ok-local-action-reference",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/setup
            """,
            []),
            new RuleCase(
            "ng-local-action-with-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/setup@v1
            """,
            ["local action uses must not contain '@ref'"]),
            new RuleCase(
            "ok-docker-action-reference",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker://rhysd/actionlint:latest
            """,
            []),
            new RuleCase(
            "ok-reusable-workflow-pinned-sha",
            """
            on: push
            jobs:
                release:
                    uses: owner/repo/.github/workflows/reusable.yml@0123456789abcdef0123456789abcdef01234567
            """,
            []),
            new RuleCase(
            "ng-reusable-workflow-branch-ref",
            """
            on: push
            jobs:
                release:
                    uses: owner/repo/.github/workflows/reusable.yml@main
            """,
            ["not pinned to a full-length commit SHA"]),
            // regression: step without run/uses produces empty uses — should not trigger unpinned-uses rule
            new RuleCase(
            "ok-empty-uses-from-parser-error",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - name: broken step with no run or uses
            """,
            []),
            // ../ prefix is not valid for reusable workflow calls (only ./ is allowed).
            // UnpinnedUsesRule silently returns; ReusableWorkflowRule owns this diagnostic.
            new RuleCase(
            "ok-reusable-workflow-dotdotslash-defers-to-reusable-rule",
            """
            on: push
            jobs:
                release:
                    uses: ../other-repo/.github/workflows/reusable.yml
            """,
            []),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases);
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_IgnoreActions_TableDriven()
    {
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("guitarrapc/setup-dotnet"), new IgnoreActionRule("my-org/*")],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-ignored-exact-match",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: guitarrapc/setup-dotnet@main
            """,
            []),
            new RuleCase(
            "ok-ignored-wildcard-match",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/some-action@v1
            """,
            []),
            new RuleCase(
            "ng-not-ignored-still-warns",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            ["not pinned to a full-length commit SHA"]),
            new RuleCase(
            "ok-ignored-reusable-workflow",
            """
            on: push
            jobs:
                release:
                    uses: guitarrapc/setup-dotnet/.github/workflows/reusable.yml@main
            """,
            []),
            new RuleCase(
            "ng-reusable-workflow-not-ignored",
            """
            on: push
            jobs:
                release:
                    uses: other-org/repo/.github/workflows/reusable.yml@main
            """,
            ["not pinned to a full-length commit SHA"]),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_IgnoreActions_Verbose_EmitsInfo()
    {
        var config = new LintConfig
        {
            Verbose = true,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("guitarrapc/setup-dotnet")],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: guitarrapc/setup-dotnet@main
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "verbose-test.yml", config);
        var infoDiags = result.Diagnostics.Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Info).ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(1);
        await Assert.That(infoDiags[0].Message).Contains("ignored");
        await Assert.That(infoDiags[0].Message).Contains("guitarrapc/setup-dotnet@main");
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_IgnoreActions_NoVerbose_NoInfo()
    {
        var config = new LintConfig
        {
            Verbose = false,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("guitarrapc/setup-dotnet")],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: guitarrapc/setup-dotnet@main
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "no-verbose-test.yml", config);
        var infoDiags = result.Diagnostics.Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Info).ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_ShowsConfigHint_OncePerOwner()
    {
        // Two steps from the same owner should produce only ONE diagnostic with Help set
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action-a@v1
                        - uses: my-org/action-b@v2
                        - uses: other-org/tool@main
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-hint-test.yml");
        var warnings = result.Diagnostics
            .Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Warning)
            .ToArray();

        // All three should warn (none are pinned to SHA)
        await Assert.That(warnings.Length).IsEqualTo(3);

        // First occurrence of my-org should have Help with config snippet
        var myOrgFirst = warnings.First(w => w.Message.Contains("my-org/action-a", StringComparison.Ordinal));
        await Assert.That(myOrgFirst.Help).IsNotNull();
        await Assert.That(myOrgFirst.Help!).Contains("my-org/*");
        await Assert.That(myOrgFirst.Help!).Contains("owner:");
        await Assert.That(myOrgFirst.Help!).DoesNotContain("ignore-actions: [\"my-org/*\"]");
        await Assert.That(myOrgFirst.Help!).Contains("ignore-actions");

        // Second occurrence of same owner should NOT have Help (deduplicated)
        var myOrgSecond = warnings.First(w => w.Message.Contains("my-org/action-b", StringComparison.Ordinal));
        await Assert.That(myOrgSecond.Help).IsNull();

        // Different owner should have its own Help
        var otherOrg = warnings.First(w => w.Message.Contains("other-org/tool", StringComparison.Ordinal));
        await Assert.That(otherOrg.Help).IsNotNull();
        await Assert.That(otherOrg.Help!).Contains("other-org/*");
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_ShowsConfigHint_ReusableWorkflow()
    {
        // Reusable workflow (job-level uses) should also get the Help hint
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                release:
                    uses: my-org/repo/.github/workflows/reusable.yml@main
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-job-test.yml");
        var warnings = result.Diagnostics
            .Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Warning)
            .ToArray();

        await Assert.That(warnings.Length).IsEqualTo(1);
        await Assert.That(warnings[0].Help).IsNotNull();
        await Assert.That(warnings[0].Help!).Contains("my-org/*");
        await Assert.That(warnings[0].Help!).Contains("owner:");
        await Assert.That(warnings[0].Help!).DoesNotContain("ignore-actions: [\"my-org/*\"]");
        await Assert.That(warnings[0].Help!).Contains("ignore-actions");
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_NoHint_WhenIgnored()
    {
        // When the action is already ignored, no warning → no hint needed
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("my-org/*")],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action-a@v1
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-no-hint-test.yml", config);
        var warnings = result.Diagnostics
            .Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Warning)
            .ToArray();

        // Should be ignored, no warning at all
        await Assert.That(warnings.Length).IsEqualTo(0);
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_CaseInsensitiveOwnerDedup()
    {
        // Same owner with different case should be deduplicated
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: MyOrg/action-a@v1
                        - uses: myorg/action-b@v2
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-case-test.yml");
        var warnings = result.Diagnostics
            .Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Warning)
            .ToArray();

        await Assert.That(warnings.Length).IsEqualTo(2);

        // First gets hint
        var first = warnings.First(w => w.Message.Contains("MyOrg/action-a", StringComparison.Ordinal));
        await Assert.That(first.Help).IsNotNull();
        await Assert.That(first.Help!).Contains("MyOrg/*");

        // Second (different case) does NOT get hint — same owner, deduplicated
        var second = warnings.First(w => w.Message.Contains("myorg/action-b", StringComparison.Ordinal));
        await Assert.That(second.Help).IsNull();
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_PreservesUtf8Owner()
    {
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: äction-org/tool@v1
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-utf8-owner-test.yml");
        var warning = result.Diagnostics
            .Single(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Warning);

        await Assert.That(warning.Help).IsNotNull();
        await Assert.That(warning.Help!).Contains("äction-org/*");
        await Assert.That(warning.Help!).DoesNotContain("?ction-org/*");
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_IgnoreActions_ProgrammaticConfig_PreservesNonAsciiOwnerCase()
    {
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("Äction-org/*")],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-programmatic-nonascii-owner-ignore",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: Äction-org/tool@v1
            """,
            []),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_NoHint_ShaPinned()
    {
        // SHA-pinned actions produce no warning → no hint
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@a5ac7e51b41094c92402da3b24376905380afc29
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-sha-test.yml");
        var warnings = result.Diagnostics
            .Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Warning)
            .ToArray();

        await Assert.That(warnings.Length).IsEqualTo(0);
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_NoHint_LocalAndDocker()
    {
        // Local and Docker uses should not produce help hints (no owner concept)
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
                        - uses: docker://alpine:3.18
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-local-docker-test.yml");
        var allDiags = result.Diagnostics
            .Where(x => x.RuleId == "unpinned-uses")
            .ToArray();

        // No unpinned-uses warnings for local/docker (local has no @ref, docker has no SHA check here)
        foreach (var d in allDiags)
        {
            await Assert.That(d.Help).IsNull();
        }
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_RefConditionalIgnore_MatchingRef_Ignored()
    {
        // Object form: ignore MyOrg/* only when ref is main or master
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("my-org/*", ["main", "master"])],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-ref-conditional-main-ignored",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action-a@main
            """,
            []),
            new RuleCase(
            "ok-ref-conditional-master-ignored",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action-b@master
            """,
            []),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_RefConditionalIgnore_NonMatchingRef_Warns()
    {
        // Object form: non-matching ref should still produce warning
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("my-org/*", ["main", "master"])],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ng-ref-conditional-v1-warns",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action-a@v1
            """,
            ["not pinned to a full-length commit SHA"]),
            new RuleCase(
            "ng-ref-conditional-develop-warns",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action-b@develop
            """,
            ["not pinned to a full-length commit SHA"]),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_RefConditionalIgnore_OwnerOnlyAndRefSpecificEntries()
    {
        // Owner-only entry ignores all refs; ref-specific entry ignores only listed refs.
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions =
                    [
                        new IgnoreActionRule("trusted-org/*"),           // all refs
                        new IgnoreActionRule("semi-trusted/*", ["main"]), // only main
                    ],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-owner-only-entry-ignores-all-refs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: trusted-org/action@v1
            """,
            []),
            new RuleCase(
            "ok-ref-specific-entry-matching-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: semi-trusted/action@main
            """,
            []),
            new RuleCase(
            "ng-object-form-non-matching-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: semi-trusted/action@v2
            """,
            ["not pinned to a full-length commit SHA"]),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_RefConditionalIgnore_ReusableWorkflow()
    {
        // Object form with reusable workflow (job-level uses)
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("my-org/*", ["main"])],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-reusable-workflow-matching-ref-ignored",
            """
            on: push
            jobs:
                release:
                    uses: my-org/repo/.github/workflows/reusable.yml@main
            """,
            []),
            new RuleCase(
            "ng-reusable-workflow-non-matching-ref",
            """
            on: push
            jobs:
                release:
                    uses: my-org/repo/.github/workflows/reusable.yml@v1
            """,
            ["not pinned to a full-length commit SHA"]),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_RefConditionalIgnore_CaseSensitiveRef()
    {
        // Refs are matched case-sensitively
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("my-org/*", ["main"])],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-exact-case-match-ignored",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action@main
            """,
            []),
            new RuleCase(
            "ng-different-case-warns",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action@Main
            """,
            ["not pinned to a full-length commit SHA"]),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }

    [Test]
    public async Task RuleRegression_UnpinnedImageRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-docker-uses-pinned-digest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker://rhysd/actionlint@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
            """,
            []),
            new RuleCase(
            "ng-docker-uses-tag",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker://rhysd/actionlint:latest
            """,
            ["not pinned by digest"]),
            new RuleCase(
            "ok-job-container-pinned-digest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/example/app@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-container-tag",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/example/app:1.0.0
                    steps:
                        - run: echo ng
            """,
            ["not pinned by digest"]),
            new RuleCase(
            "ng-job-container-implicit-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/example/app
                    steps:
                        - run: echo ng
            """,
            ["not pinned by digest"]),
            new RuleCase(
            "ok-service-container-pinned-digest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        db:
                            image: postgres@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-service-container-tag",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        db:
                            image: postgres:16
                    steps:
                        - run: echo ng
            """,
            ["not pinned by digest"]),
            new RuleCase(
            "ok-non-docker-uses-is-ignored",
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

        await AssertRuleCases(new UnpinnedImageRule(), "unpinned-image", cases);
    }

    [Test]
    public async Task RuleRegression_DangerousTriggersRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-push",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-pull-request",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-pull-request-target",
            """
            on: pull_request_target
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["potentially dangerous"]),
            new RuleCase(
            "ng-workflow-run",
            """
            on: workflow_run
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["potentially dangerous"]),
            new RuleCase(
            "ng-multiple-dangerous-triggers",
            """
            on:
                pull_request_target:
                workflow_run:
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["potentially dangerous"]),
        };

        await AssertRuleCases(new DangerousTriggersRule(), "dangerous-triggers", cases);
    }

    [Test]
    public async Task RuleRegression_JobPermissionsRequiredRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-permissions-defined",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions:
                        contents: read
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-permissions-read-all",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: read-all
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-reusable-workflow-job-with-permissions",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
                    permissions:
                        contents: read
            """,
            []),
            new RuleCase(
            "ng-reusable-workflow-job-no-permissions",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
            """,
            ["does not have permissions defined"]),
            new RuleCase(
            "ng-no-permissions",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["does not have permissions defined"]),
            new RuleCase(
            "ng-multiple-jobs-one-missing",
            """
            on: push
            jobs:
                ok-job:
                    runs-on: ubuntu-latest
                    permissions:
                        contents: read
                    steps:
                        - run: echo ok
                ng-job:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["does not have permissions defined"]),
        };

        await AssertRuleCases(new JobPermissionsRequiredRule(), "job-permissions-required", cases);
    }

    [Test]
    public async Task RuleRegression_NeedsGraphRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-no-needs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-needs-valid-job",
            """
            on: push
            jobs:
                setup:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo setup
                build:
                    needs: setup
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo build
            """,
            []),
            new RuleCase(
            "ok-needs-multiple-valid",
            """
            on: push
            jobs:
                setup:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo setup
                test:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo test
                deploy:
                    needs: [setup, test]
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo deploy
            """,
            []),
            new RuleCase(
            "ng-needs-unknown-job",
            """
            on: push
            jobs:
                build:
                    needs: nonexistent
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["references unknown job"]),
            new RuleCase(
            "ng-needs-one-of-multiple-unknown",
            """
            on: push
            jobs:
                setup:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo setup
                build:
                    needs: [setup, ghost]
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["references unknown job"]),
            new RuleCase(
            "ng-self-reference",
            """
            on: push
            jobs:
                build:
                    needs: build
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["cyclic dependencies in \"needs\" job configurations are detected"]),
            new RuleCase(
            "ng-two-job-cycle",
            """
            on: push
            jobs:
                a:
                    needs: b
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo a
                b:
                    needs: a
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo b
            """,
            ["cyclic dependencies in \"needs\" job configurations are detected"]),
            new RuleCase(
            "ng-three-job-cycle",
            """
            on: push
            jobs:
                a:
                    needs: b
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo a
                b:
                    needs: c
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo b
                c:
                    needs: a
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo c
            """,
            ["cyclic dependencies in \"needs\" job configurations are detected"]),
        };

        await AssertRuleCases(new NeedsGraphRule(), "needs-graph", cases);
    }

    [Test]
    public async Task RuleRegression_NeedsGraphRule_DuplicateNeeds_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-duplicate-needs-id",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo build
                test:
                    runs-on: ubuntu-latest
                    needs: [build, build]
                    steps:
                        - run: echo test
            """,
            ["duplicates"]),
            new RuleCase(
            "ok-unique-needs-ids",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo build
                lint:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo lint
                test:
                    runs-on: ubuntu-latest
                    needs: [build, lint]
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "ng-duplicate-needs-case-insensitive",
            """
            on: push
            jobs:
                bar:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo bar
                foo:
                    needs: [bar, BAR]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo foo
            """,
            ["duplicates"]),
        };

        await AssertRuleCases(new NeedsGraphRule(), "needs-graph", cases);
    }

    [Test]
    public async Task RuleRegression_NeedsGraphRule_CyclePosition()
    {
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                from:
                    needs: [to]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo from
                to:
                    needs: [from]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo to
            """);

        using var result = new LintEngine([new NeedsGraphRule()]).Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var diags = result.Diagnostics.Where(x => x.RuleId == "needs-graph" && x.Message.Contains("cyclic")).ToArray();

        await Assert.That(diags.Length).IsGreaterThanOrEqualTo(1);

        // The cycle is reported at the first job in the cycle path (consistent with actionlint positioning).
        // DFS visits "from" first, detects cycle "from" -> "to" -> "from".
        // Report is at the first job in the cycle ("from" at line 3).
        var cycleD = diags[0];
        await Assert.That(cycleD.Location.StartLine).IsEqualTo(3);
        // Message should include cycle path
        await Assert.That(cycleD.Message).Contains("\"from\" -> \"to\" -> \"from\"");
    }

    [Test]
    public async Task RuleRegression_ShellNameRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-bash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: bash
            """,
            []),
            new RuleCase(
            "ok-pwsh",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: pwsh
            """,
            []),
            new RuleCase(
            "ok-powershell",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: powershell
            """,
            []),
            new RuleCase(
            "ok-sh",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: sh
            """,
            []),
            new RuleCase(
            "ok-cmd",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: cmd
            """,
            []),
            new RuleCase(
            "ok-python",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: print('ok')
                          shell: python
            """,
            []),
            new RuleCase(
            "ok-expression-skipped",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: ${{ inputs.shell }}
            """,
            []),
            new RuleCase(
            "ok-no-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-invalid-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
                          shell: zsh
            """,
            ["shell name", "invalid"]),
            new RuleCase(
            "ng-empty-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
                          shell: ''
            """,
            ["shell name", "invalid"]),
            new RuleCase(
            "ok-workflow-defaults-bash",
            """
            on: push
            defaults:
                run:
                    shell: bash
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-job-defaults-pwsh",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    permissions: {}
                    defaults:
                        run:
                            shell: pwsh
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-defaults-invalid-shell",
            """
            on: push
            defaults:
                run:
                    shell: zsh
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            ["shell name", "invalid"]),
            new RuleCase(
            "ng-job-defaults-invalid-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    defaults:
                        run:
                            shell: fish
                    steps:
                        - run: echo ok
            """,
            ["shell name", "invalid"]),
            new RuleCase(
            "ok-custom-shell-template-perl",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: print "ok"
                          shell: perl {0}
            """,
            []),
            new RuleCase(
            "ok-custom-shell-template-ruby",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: puts 'ok'
                          shell: ruby {0}
            """,
            []),
        };

        await AssertRuleCases(new ShellNameRule(), "shell-name", cases);
    }

    [Test]
    public async Task RuleRegression_ShellNameRule_OsSpecific_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-cmd-on-ubuntu",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                          shell: cmd
            """,
            ["cmd", "not available on"]),
            new RuleCase(
            "ng-powershell-on-ubuntu",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                          shell: powershell
            """,
            ["powershell", "not available on"]),
            new RuleCase(
            "ok-pwsh-on-ubuntu",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                          shell: pwsh
            """,
            []),
            new RuleCase(
            "ok-cmd-on-windows",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    steps:
                        - run: echo ok
                          shell: cmd
            """,
            []),
            new RuleCase(
            "ng-sh-on-windows",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    steps:
                        - run: echo ng
                          shell: sh
            """,
            ["sh", "not available on"]),
            new RuleCase(
            "ok-sh-on-ubuntu",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                          shell: sh
            """,
            []),
        };

        await AssertRuleCases(new ShellNameRule(), "shell-name", cases);
    }

    [Test]
    public async Task RuleRegression_RunnerLabelRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-ubuntu-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-windows-2022",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-2022
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-macos-14",
            """
            on: push
            jobs:
                build:
                    runs-on: macos-14
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-self-hosted-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: [self-hosted, linux, x64, custom-runner]
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-runs-on-expression-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.runner }}
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-unknown-ubuntu-label",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-9999
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["is unknown. available labels are"]),
            new RuleCase(
            "ng-unknown-mapping-label",
            """
            on: push
            jobs:
                build:
                    runs-on:
                        labels: [custom-hosted]
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["is unknown. available labels are"]),
            new RuleCase(
            "ok-mapping-labels-with-self-hosted-skip",
            """
            on: push
            jobs:
                build:
                    runs-on:
                        labels: [self-hosted, custom-hosted]
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new RunnerLabelRule(), "runner-label", cases);
    }

    [Test]
    public async Task RuleRegression_RunnerLabelRule_MatrixExpanded_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-matrix-unknown-scalar",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - macos-latest
                                - linux-latest
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo test
            """,
            ["is unknown. available labels are"]),
            new RuleCase(
            "ok-matrix-known-labels-only",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - ubuntu-latest
                                - macos-latest
                                - windows-latest
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-matrix-self-hosted-array",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - [self-hosted, linux, x64]
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-matrix-self-hosted-preset-label",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - arm64
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-matrix-gpu-unknown",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - macos-latest
                                - gpu
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo test
            """,
            ["is unknown. available labels are"]),
            new RuleCase(
            "ok-matrix-expression-row-skip",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner: ${{ fromJson(needs.setup.outputs.runners) }}
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-matrix-no-strategy-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-matrix-mixed-known-and-self-hosted",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - ubuntu-latest
                                - [self-hosted, linux, x64]
                                - arm64
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-non-matrix-expression-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ github.event.inputs.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new RunnerLabelRule(), "runner-label", cases);
    }

    [Test]
    public async Task RuleRegression_RunnerLabelRule_OsConflict_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-mixed-os-labels",
            """
            on: push
            jobs:
                build:
                    runs-on: [ubuntu-latest, windows-latest]
                    steps:
                        - run: echo ng
            """,
            ["\"windows-latest\" conflicts with label \"ubuntu-latest\""]),
            new RuleCase(
            "ng-multiple-os-conflicts",
            """
            on: push
            jobs:
                build:
                    runs-on: [ubuntu-latest, windows-latest, macos-latest]
                    steps:
                        - run: echo ng
            """,
            ["\"windows-latest\" conflicts with label \"ubuntu-latest\"", "\"macos-latest\" conflicts with label \"ubuntu-latest\""]),
            new RuleCase(
            "ng-bare-os-label-conflict",
            """
            on: push
            jobs:
                build:
                    runs-on: [ubuntu-latest, windows]
                    steps:
                        - run: echo ng
            """,
            ["\"windows\" conflicts with label \"ubuntu-latest\""]),
            new RuleCase(
            "ok-single-os-label",
            """
            on: push
            jobs:
                build:
                    runs-on: [ubuntu-latest]
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new RunnerLabelRule(), "runner-label", cases);
    }

    [Test]
    public async Task RuleRegression_RunnerLabelRule_MatrixOsConflict_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-matrix-os-conflict-with-static",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [windows-latest, macos-latest]
                    runs-on: [ubuntu-latest, '${{matrix.os}}']
                    steps:
                        - run: echo ng
            """,
            ["\"windows-latest\" conflicts with label \"ubuntu-latest\"", "\"macos-latest\" conflicts with label \"ubuntu-latest\""]),
            new RuleCase(
            "ng-matrix-os-conflict-bare-label",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [windows-latest, macos-latest, windows]
                    runs-on: [ubuntu-latest, '${{matrix.os}}']
                    steps:
                        - run: echo ng
            """,
            ["\"windows-latest\" conflicts with label \"ubuntu-latest\"", "\"macos-latest\" conflicts with label \"ubuntu-latest\"", "\"windows\" conflicts with label \"ubuntu-latest\""]),
            new RuleCase(
            "ok-matrix-same-os-family",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [ubuntu-22.04, ubuntu-24.04]
                    runs-on: [ubuntu-latest, '${{matrix.os}}']
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new RunnerLabelRule(), "runner-label", cases);
    }

    [Test]
    public async Task RuleRegression_RunnerNoLatestRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-ubuntu-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["moving latest label"]),
            new RuleCase(
            "ng-windows-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    steps:
                        - run: echo ng
            """,
            ["moving latest label"]),
            new RuleCase(
            "ng-macos-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: macos-latest
                    steps:
                        - run: echo ng
            """,
            ["moving latest label"]),
            new RuleCase(
            "ok-version-pinned-label",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-24.04
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-self-hosted-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: [self-hosted, linux, x64]
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-runs-on-expression-skip",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new RunnerNoLatestRule(), "runner-no-latest", cases);
    }

    [Test]
    public async Task RuleRegression_IdNamingRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-valid-job-and-step-ids",
            """
            on: push
            jobs:
                Build_123:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: setup-1
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-no-step-id",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-step-id-expression-skipped",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: ${{ matrix.step_id }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-id-with-space",
            """
            on: push
            jobs:
                "bad id":
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid job ID", "must start with a letter"]),
            new RuleCase(
            "ng-step-id-with-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: setup.v1
                          run: echo ng
            """,
            ["invalid step ID", "must start with a letter"]),
            new RuleCase(
            "ng-step-id-empty",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: ''
                          run: echo ng
            """,
            ["step ID should not be empty"]),
            new RuleCase(
            "ng-job-id-starts-with-digit",
            """
            on: push
            jobs:
                1build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid job ID", "must start with a letter"]),
            new RuleCase(
            "ng-step-id-starts-with-dash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: -setup
                          run: echo ng
            """,
            ["invalid step ID", "must start with a letter"]),
            new RuleCase(
            "ng-step-id-duplicate-case-insensitive",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - id: BuildStep
                          run: echo first
                        - id: buildstep
                          run: echo second
            """,
            ["duplicated in the same job", "case-insensitive"]),
        };

        await AssertRuleCases(new IdNamingRule(), "id-naming", cases);
    }

    [Test]
    public async Task RuleRegression_DispatchInputsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-choice-with-options-and-default",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: choice
                            options: [dev, prod]
                            default: dev
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-choice-without-options",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: choice
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["type 'choice' must define non-empty options"]),
            new RuleCase(
            "ng-choice-duplicate-options",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: choice
                            options: [dev, dev]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["has duplicated option"]),
            new RuleCase(
            "ng-choice-default-not-in-options",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: choice
                            options: [dev, prod]
                            default: staging
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["default value 'staging'", "not included in options"]),
            new RuleCase(
            "ng-non-choice-has-options",
            """
            on:
                workflow_dispatch:
                    inputs:
                        count:
                            type: number
                            options: [1, 2]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["has options but type is"]),
            new RuleCase(
            "ng-number-default-not-number",
            """
            on:
                workflow_dispatch:
                    inputs:
                        count:
                            type: number
                            default: NaNValue
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["is not a valid number"]),
            new RuleCase(
            "ng-boolean-default-invalid",
            """
            on:
                workflow_dispatch:
                    inputs:
                        force:
                            type: boolean
                            default: yes
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["must be 'true' or 'false'"]),
            new RuleCase(
            "ng-more-than-25-inputs",
            """
            on:
                workflow_dispatch:
                    inputs:
                        i01: { type: string }
                        i02: { type: string }
                        i03: { type: string }
                        i04: { type: string }
                        i05: { type: string }
                        i06: { type: string }
                        i07: { type: string }
                        i08: { type: string }
                        i09: { type: string }
                        i10: { type: string }
                        i11: { type: string }
                        i12: { type: string }
                        i13: { type: string }
                        i14: { type: string }
                        i15: { type: string }
                        i16: { type: string }
                        i17: { type: string }
                        i18: { type: string }
                        i19: { type: string }
                        i20: { type: string }
                        i21: { type: string }
                        i22: { type: string }
                        i23: { type: string }
                        i24: { type: string }
                        i25: { type: string }
                        i26: { type: string }
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["maximum number of inputs", "25 but 26"]),
        };

        await AssertRuleCases(new DispatchInputsRule(), "dispatch-inputs", cases);
    }

    [Test]
    public async Task RuleRegression_WorkflowCallInputDefaultRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-boolean-input-non-bool-default",
            """
            on:
                workflow_call:
                    inputs:
                        debug:
                            type: boolean
                            default: "yes"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["boolean", "default"]),
            new RuleCase(
            "ng-number-input-non-number-default",
            """
            on:
                workflow_call:
                    inputs:
                        retries:
                            type: number
                            default: "three"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["number", "default"]),
            new RuleCase(
            "ok-boolean-input-true-default",
            """
            on:
                workflow_call:
                    inputs:
                        debug:
                            type: boolean
                            default: true
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-string-input-any-default",
            """
            on:
                workflow_call:
                    inputs:
                        name:
                            type: string
                            default: "hello"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-required-input-with-default",
            """
            on:
                workflow_call:
                    inputs:
                        path:
                            type: string
                            required: true
                            default: ""
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["default", "required"]),
            new RuleCase(
            "ok-required-input-without-default",
            """
            on:
                workflow_call:
                    inputs:
                        path:
                            type: string
                            required: true
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new WorkflowCallInputDefaultRule(), "workflow-call-input-default", cases);
    }

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

    [Test]
    public async Task RuleRegression_ScheduleEventRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-valid-cron",
            """
            on:
                schedule:
                    - cron: "*/5 * * * *"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-invalid-cron-syntax",
            """
            on:
                schedule:
                    - cron: "* * * *"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["cron", "invalid", "exactly 5 fields"]),
            new RuleCase(
            "ng-cron-too-frequent",
            """
            on:
                schedule:
                    - cron: "* * * * *"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["runs too frequently", "once per", "once every 5 minutes"]),
            new RuleCase(
            "ng-invalid-timezone",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: "Mars/Phobos"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "invalid"]),
            new RuleCase(
            "ng-iana-like-invalid-timezone",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: "Asia/Somewhere"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "invalid"]),
            new RuleCase(
            "ng-typo-timezone-did-you-mean",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: "Asia/Toky"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "invalid", "did you mean", "Asia/Tokyo"]),
            new RuleCase(
            "ng-empty-timezone",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: ""
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "must not be empty"]),
            new RuleCase(
            "ng-empty-cron",
            """
            on:
                schedule:
                    - cron: ""
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["cron", "must not be empty"]),
            new RuleCase(
            "ng-extremely-long-timezone",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "invalid"]),
        };

        await AssertRuleCases(new ScheduleEventRule(), "schedule-event", cases);
    }

    [Test]
    public async Task RuleRegression_GlobPatternRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-valid-branch-and-path-glob",
            """
            on:
                pull_request:
                    branches: [main, release/**]
                    paths: ['src/**', '!docs/**']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-expression-skipped",
            """
            on:
                push:
                    branches:
                        - ${{ github.ref_name }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-triple-star-in-branches",
            """
            on:
                push:
                    branches: ['feature/***']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "consecutive '*'"]),
            new RuleCase(
            "ng-unclosed-class-in-paths-ignore",
            """
            on:
                pull_request:
                    paths-ignore:
                        - 'src/[abc'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "missing ]"]),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
    }

    [Test]
    public async Task RuleRegression_GlobPatternRule_Syntax_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-reversed-bracket-range",
            """
            on:
                push:
                    branches: ['feature/[z-a]']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["start of range", "is larger than end of range"]),
            new RuleCase(
            "ng-dot-dot-path-segment",
            """
            on:
                push:
                    paths: ['src/../etc/passwd']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["'.' and '..' are not allowed"]),
            new RuleCase(
            "ng-caret-char-in-branch-pattern",
            """
            on:
                push:
                    branches: ['^foo-']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["character '^' is invalid"]),
            new RuleCase(
            "ng-star-plus-in-tag-pattern",
            """
            on:
                push:
                    tags: ['v*+']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["unexpected character '+' after '*'"]),
            new RuleCase(
            "ng-dot-path-segment",
            """
            on:
                push:
                    paths: ['./foo/bar.txt']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["'.' and '..' are not allowed"]),
            new RuleCase(
            "ok-valid-bracket-range",
            """
            on:
                push:
                    branches: ['release/v[0-9].*']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-backslash-regex-escape-in-tags",
            """
            on:
                push:
                    tags: ['v\d+']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid for branch and tag names", "can be escaped"]),
            new RuleCase(
            "ng-trailing-backslash-in-branches",
            """
            on:
                push:
                    branches: ["feature\\"]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "trailing backslash"]),
            new RuleCase(
            "ok-valid-backslash-escape-star",
            """
            on:
                push:
                    branches: ['feature/\*']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-lone-bang-in-tags",
            """
            on:
                push:
                    tags: ['!']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["at least one character must follow !"]),
            new RuleCase(
            "ng-glob-errors-detected-after-null-entry-in-paths",
            """
            on:
                push:
                    paths:
                        -
                        - '!'
                        - '  foo'
                        - '.'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["at least one character must follow !", "leading and trailing spaces", "'.' and '..' are not allowed"]),
            new RuleCase(
            "ng-leading-space-in-paths",
            """
            on:
                push:
                    paths: ['  foo']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["leading and trailing spaces"]),
            new RuleCase(
            "ng-trailing-space-in-paths",
            """
            on:
                push:
                    paths: ['foo  ']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["leading and trailing spaces"]),
            new RuleCase(
            "ng-space-only-in-paths",
            """
            on:
                push:
                    paths: [' ']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["leading and trailing spaces"]),
            new RuleCase(
            "ok-space-in-branches-is-ref-error",
            """
            on:
                push:
                    branches: [' ']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid for branch and tag names"]),
            new RuleCase(
            "ng-ref-starts-with-slash",
            """
            on:
                push:
                    tags: ['/v1.0']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["ref name must not start with /"]),
            new RuleCase(
            "ng-ref-ends-with-slash",
            """
            on:
                push:
                    branches: ['feature/']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["ref name must not end with /"]),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
    }

    [Test]
    public async Task RuleRegression_GlobPatternRule_SnapshotVersion_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-unclosed-bracket-in-snapshot-version",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    snapshot:
                        image-name: my-image
                        version: 'v[0-'
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "missing ]"]),
            new RuleCase(
            "ok-valid-snapshot-version",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    snapshot:
                        image-name: my-image
                        version: 'v1.2.3'
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
    }

    [Test]
    public async Task RuleRegression_GlobPatternRule_ImageVersionVersions_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-unclosed-bracket-in-image-version-versions",
            """
            on:
                image_version:
                    versions:
                        - 'v[0-'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "missing ]"]),
            new RuleCase(
            "ng-lone-bang-in-image-version-versions",
            """
            on:
                image_version:
                    versions:
                        - '!'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["at least one character must follow !"]),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
    }

    [Test]
    public async Task RuleRegression_DenyWriteAllRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-workflow-read-all",
            """
            on: push
            permissions: read-all
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-job-scopes-only",
            """
            on: push
            jobs:
                build:
                    permissions:
                        contents: write
                        actions: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-write-all",
            """
            on: push
            permissions: write-all
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'write-all' is forbidden"]),
            new RuleCase(
            "ng-job-write-all",
            """
            on: push
            jobs:
                build:
                    permissions: write-all
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'write-all' is forbidden"]),
        };

        await AssertRuleCases(new DenyWriteAllRule(), "deny-write-all", cases);
    }

    [Test]
    public async Task RuleRegression_DenyReadAllRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-workflow-explicit-scopes",
            """
            on: push
            permissions:
                contents: read
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-job-write-all-not-target",
            """
            on: push
            jobs:
                build:
                    permissions: write-all
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-read-all",
            """
            on: push
            permissions: read-all
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'read-all' is forbidden"]),
            new RuleCase(
            "ng-job-read-all",
            """
            on: push
            jobs:
                build:
                    permissions: read-all
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permissions scalar 'read-all' is forbidden"]),
        };

        await AssertRuleCases(new DenyReadAllRule(), "deny-read-all", cases);
    }

    [Test]
    public async Task RuleRegression_JobTimeoutMinutesRequiredRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-job-timeout-present",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    timeout-minutes: 15
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-step-timeout-on-all-steps",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - timeout-minutes: 3
                          run: echo ok
                        - timeout-minutes: 5
                          uses: actions/checkout@v4
            """,
            []),
            new RuleCase(
            "ok-reusable-workflow-call-not-target",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
            """,
            []),
            new RuleCase(
            "ng-missing-job-and-step-timeouts",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
                        - uses: actions/checkout@v4
            """,
            ["should define timeout-minutes", "default is 360 minutes", "set timeout-minutes on each step instead"]),
        };

        await AssertRuleCases(new JobTimeoutMinutesRequiredRule(), "job-timeout-minutes-required", cases);
    }

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

    [Test]
    public async Task RuleRegression_WorkflowSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-single-job-workflow-exception",
            """
            on: push
            env:
                GITHUB_TOKEN: ${{ github.token }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-multi-job-non-secret-env",
            """
            on: push
            env:
                NORMAL_VALUE: plain
            jobs:
                a:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo a
                b:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo b
            """,
            []),
            new RuleCase(
            "ng-multi-job-github-token-in-workflow-env",
            """
            on: push
            env:
                GITHUB_TOKEN: ${{ github.token }}
            jobs:
                a:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo a
                b:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo b
            """,
            ["must not set secrets.* or github.token", "move secret mapping to job/step env"]),
            new RuleCase(
            "ng-multi-job-secrets-in-workflow-env",
            """
            on: push
            env:
                DATADOG_API_KEY: ${{ secrets.DATADOG_API_KEY }}
            jobs:
                a:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo a
                b:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo b
            """,
            ["must not set secrets.* or github.token", "DATADOG_API_KEY"]),
        };

        await AssertRuleCases(new WorkflowSecretsRule(), "workflow-secrets", cases);
    }

    [Test]
    public async Task RuleRegression_JobSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-single-step-job-exception",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        GITHUB_TOKEN: ${{ github.token }}
                    steps:
                        - run: echo only-step
            """,
            []),
            new RuleCase(
            "ok-multi-step-non-secret-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        NORMAL_VALUE: plain
                    steps:
                        - run: echo first
                        - run: echo second
            """,
            []),
            new RuleCase(
            "ng-multi-step-github-token-in-job-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        GITHUB_TOKEN: ${{ github.token }}
                    steps:
                        - run: echo first
                        - run: echo second
            """,
            ["must not set secrets.* or github.token", "step env"]),
            new RuleCase(
            "ng-multi-step-secrets-in-job-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        DATADOG_API_KEY: ${{ secrets.DATADOG_API_KEY }}
                    steps:
                        - run: echo first
                        - run: echo second
            """,
            ["must not set secrets.* or github.token", "DATADOG_API_KEY"]),
        };

        await AssertRuleCases(new JobSecretsRule(), "job-secrets", cases);
    }

    [Test]
    public async Task RuleRegression_ActionShellIsRequiredRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
                        "ok-action-run-with-shell",
                        """
                        name: Sample action
                        runs:
                            using: composite
                            steps:
                                - run: echo hello
                                    shell: bash
                        """,
                        []),
                        new RuleCase(
                        "ok-run-with-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
                          shell: bash
            """,
            []),
            new RuleCase(
            "ok-action-step-no-run",
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
            "ok-workflow-run-without-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
            """,
            []),
            new RuleCase(
            "ok-workflow-run-with-empty-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
                          shell: ""
            """,
            []),
        };

        await AssertRuleCases(new ActionShellIsRequiredRule(), "action-shell-is-required", cases);
    }

    [Test]
    public async Task RuleRegression_CachePoisoningRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-cache-on-trusted-trigger",
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
            new RuleCase(
            "ng-cache-on-pull-request",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            ["cache action", "untrusted triggers"]),
            new RuleCase(
            "ng-cache-restore-on-workflow-run",
            """
            on: workflow_run
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache/restore@v4
                          with:
                              path: ~/.npm
                              key: npm-${{ runner.os }}
            """,
            ["cache action", "untrusted triggers"]),
        };

        await AssertRuleCases(new CachePoisoningRule(), "cache-poisoning", cases);
    }

    [Test]
    public async Task RuleRegression_SelfHostedRunnerRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-self-hosted-on-push",
            """
            on: push
            jobs:
                build:
                    runs-on: self-hosted
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-self-hosted-on-pull-request",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: self-hosted
                    steps:
                        - run: echo ok
            """,
            ["self-hosted runner", "untrusted triggers"]),
            new RuleCase(
            "ng-self-hosted-on-workflow-run",
            """
            on: workflow_run
            jobs:
                build:
                    runs-on:
                        - self-hosted
                        - linux
                    steps:
                        - run: echo ok
            """,
            ["self-hosted runner", "untrusted triggers"]),
            new RuleCase(
            "ng-self-hosted-message-has-runs-on-path",
            """
            on: pull_request
            jobs:
                ci:
                    runs-on: self-hosted
                    steps:
                        - run: echo ok
            """,
            ["jobs.'ci'.runs-on"]),
        };

        await AssertRuleCases(new SelfHostedRunnerRule(), "self-hosted-runner", cases);
    }

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

    [Test]
    public async Task RuleRegression_SecretsOutsideEnvRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-secret-in-env-handoff",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        TOKEN: ${{ secrets.GITHUB_TOKEN }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-secret-in-step-if",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ secrets.GITHUB_TOKEN != '' }}
                          run: echo ng
            """,
            ["step.if", "secrets context"]),
            new RuleCase(
            "ok-secret-in-action-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/github-script@v7
                          with:
                              script: ${{ secrets.GITHUB_TOKEN }}
            """,
            []),
            new RuleCase(
            "ok-secret-in-create-github-app-token-inputs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/create-github-app-token@v2
                          with:
                              app-id: ${{ secrets.APP_ID }}
                              private-key: ${{ secrets.PRIVATE_KEY }}
            """,
            []),
        };

        await AssertRuleCases(new SecretsOutsideEnvRule(), "secrets-outside-env", cases);
    }

    [Test]
    public async Task RuleRegression_MatrixRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-small-matrix",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                            node: [20]
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-empty-axis",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: []
                    steps:
                        - run: echo ng
            """,
            ["strategy.matrix axis 'os' has no values"]),
            new RuleCase(
            "ok-include-new-axis",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                            include:
                                - arch: x64
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-include-mixed-existing-and-new-axes",
            """
            on: push
            jobs:
                dispatch:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            repo: [guitarrapc/testtest]
                            include:
                                - repo: guitarrapc/testtest
                                  ref: main
                                  workflow: test
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-exclude-unknown-axis",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                            exclude:
                                - arch: x64
                    steps:
                        - run: echo ng
            """,
            ["strategy.matrix.exclude references unknown axis 'arch'"]),
        };

        await AssertRuleCases(new MatrixRule(), "matrix", cases);
    }

    [Test]
    public async Task RuleRegression_MatrixRule_DuplicateValues_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-duplicate-axis-value",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-20.04, ubuntu-22.04, ubuntu-20.04]
                    steps:
                        - run: echo ng
            """,
            ["duplicate"]),
            new RuleCase(
            "ok-unique-axis-values",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-20.04, ubuntu-22.04, ubuntu-24.04]
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new MatrixRule(), "matrix", cases);
    }

    [Test]
    public async Task RuleRegression_MatrixRule_ExcludeValueMismatch_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-scalar-value-mismatch",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            node: [10, 12, 14]
                            os: [ubuntu-latest, macos-latest]
                            exclude:
                                - node: 13
                                  os: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["does not match in matrix \"node\" combinations"]),
            new RuleCase(
            "ok-scalar-value-matches",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            node: [10, 12, 14]
                            os: [ubuntu-latest, macos-latest]
                            exclude:
                                - node: 10
                                  os: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-exclude-value-is-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            foo: [aaa]
                            exclude:
                                - foo: ${{ fromJSON('"x"') }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-row-value-is-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            foo:
                                - ${{ fromJSON('{"bar":"x"}') }}
                            exclude:
                                - foo: bar
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-include-only-axis-value-mismatch",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                            include:
                                - os: ubuntu-latest
                                  gui: gnome
                            exclude:
                                - os: ubuntu-latest
                                  gui: kde
                    steps:
                        - run: echo ng
            """,
            ["does not match in matrix \"gui\" combinations"]),
            new RuleCase(
            "ok-include-only-axis-value-matches",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                            include:
                                - os: ubuntu-latest
                                  gui: gnome
                            exclude:
                                - os: ubuntu-latest
                                  gui: gnome
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new MatrixRule(), "matrix", cases);
    }

    [Test]
    public async Task RuleRegression_MatrixRule_ExcludeObjectValueReportsAtValueLine()
    {
        // Object value in exclude: diagnostic must point to the exclude entry line, not the matrix range
        var yaml = """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.os.runner }}
                    strategy:
                        matrix:
                            os:
                                - {'runner': 'ubuntu-latest'}
                            exclude:
                                - os: {'runner': 'windows-latest'}
                    steps:
                        - run: echo ng
            """
            .Replace("\r\n", "\n");
        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "exclude-obj.yml");
        var diag = result.Diagnostics.FirstOrDefault(d => d.Message.Contains("does not match"));
        await Assert.That(diag.Message).IsNotNull();
        // The exclude entry is on line 10-11 area — diagnostic must not be on line 7 (matrix range)
        await Assert.That(diag.Location.StartLine).IsGreaterThanOrEqualTo(10);
    }

    [Test]
    public async Task RuleRegression_MatrixRule_ExcludeArrayValueReportsAtValueLine()
    {
        // Array value in exclude: diagnostic must point to the exclude entry line, not the matrix range
        var yaml = """
            on: push
            jobs:
                build:
                    runs-on: ${{ matrix.os[0] }}
                    strategy:
                        matrix:
                            os:
                                - ['ubuntu', 'latest']
                            exclude:
                                - os: ['macos', 'latest']
                    steps:
                        - run: echo ng
            """
            .Replace("\r\n", "\n");
        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "exclude-arr.yml");
        var diag = result.Diagnostics.FirstOrDefault(d => d.Message.Contains("does not match"));
        await Assert.That(diag.Message).IsNotNull();
        // The exclude entry is on line 10-11 area — diagnostic must not be on line 7 (matrix range)
        await Assert.That(diag.Location.StartLine).IsGreaterThanOrEqualTo(10);
    }

    [Test]
    public async Task RuleRegression_EnvVarRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-portable-env-keys",
            """
            on: push
            env:
                GLOBAL_TOKEN: x
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        JOB_TOKEN_1: x
                    steps:
                        - env:
                              STEP_TOKEN: x
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-env-key-lowercase",
            """
            on: push
            env:
                github_token: x
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["workflow.env key 'github_token' is not portable"]),
            new RuleCase(
            "ng-step-env-key-dash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                              TOKEN-NAME: x
                          run: echo ng
            """,
            ["step.env key 'TOKEN-NAME' is not portable"]),
        };

        await AssertRuleCases(new EnvVarRule(), "env-var", cases);
    }

    [Test]
    public async Task RuleRegression_DeprecatedCommandsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-modern-output-file",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "result=ok" >> "$GITHUB_OUTPUT"
            """,
            []),
            new RuleCase(
            "ng-set-output-command",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "::set-output name=result::ok"
            """,
            ["workflow command \"set-output\" was deprecated", "$GITHUB_OUTPUT"]),
            new RuleCase(
            "ng-set-env-command",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "::set-env name=TOKEN::x"
            """,
            ["workflow command \"set-env\" was deprecated", "$GITHUB_ENV"]),
            // regression: multi-line run script should report all deprecated commands
            new RuleCase(
            "ng-multiline-multiple-deprecated",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: |
                            echo "::set-output name=foo::bar"
                            echo "::set-env name=TOKEN::x"
            """,
            ["workflow command \"set-output\" was deprecated", "workflow command \"set-env\" was deprecated"]),
        };

        await AssertRuleCases(new DeprecatedCommandsRule(), "deprecated-commands", cases);
    }

    [Test]
    public async Task RuleRegression_IfCondRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-dynamic-condition",
            """
            on: push
            jobs:
                build:
                    if: ${{ github.ref != '' }}
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ success() }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-if-constant-false",
            """
            on: push
            jobs:
                build:
                    if: ${{ false }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["constant expression \"false\" in condition. remove the if: section"]),
            new RuleCase(
            "ng-step-if-constant-true",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ !false }}
                          run: echo ng
            """,
            ["constant expression \"!false\" in condition. remove the if: section"]),
            new RuleCase(
            "ng-step-if-always-true-multi-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ github.event_name == 'push' }} && ${{ github.ref_name == 'main' }}
                          run: echo ng
            """,
            ["always evaluated to true because extra characters are around"]),
            new RuleCase(
            "ng-step-if-always-true-trailing-space",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: "${{ github.event_name == 'push' }} "
                          run: echo ng
            """,
            ["always evaluated to true because extra characters are around"]),
            new RuleCase(
            "ok-step-if-bare-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.event_name == 'push'
                          run: echo ok
            """,
            []),
            // regression: null literal should be detected as constant (falsy)
            new RuleCase(
            "ng-step-if-null-literal",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ null }}
                          run: echo ng
            """,
            ["constant expression \"null\" in condition. remove the if: section"]),
            // regression: number literal should be detected as constant (0 = falsy)
            new RuleCase(
            "ng-step-if-number-zero",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ 0 }}
                          run: echo ng
            """,
            ["constant expression \"0\" in condition. remove the if: section"]),
            // regression: non-zero number is truthy
            new RuleCase(
            "ng-step-if-number-truthy",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ 42 }}
                          run: echo ng
            """,
            ["constant expression \"42\" in condition. remove the if: section"]),
            // regression: empty string literal is falsy
            new RuleCase(
            "ng-step-if-empty-string",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ '' }}
                          run: echo ng
            """,
            ["constant expression \"''\" in condition. remove the if: section"]),
            // regression: non-empty string literal is truthy
            new RuleCase(
            "ng-step-if-nonempty-string",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ 'hello' }}
                          run: echo ng
            """,
            ["constant expression \"'hello'\" in condition. remove the if: section"]),
            // regression: mixed type constant expression (true && 42 || !null)
            new RuleCase(
            "ng-step-if-mixed-constant",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: true && 42 || !null
                          run: echo ng
            """,
            ["constant expression \"true && 42 || !null\" in condition. remove the if: section"]),
            // regression: pure function with constant args (contains + format)
            new RuleCase(
            "ng-step-if-constant-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ contains(format('{0} {1} {2}', 'foo', 'bar', 'piyo'), 'o b') }}
                          run: echo ng
            """,
            ["constant expression"]),
            // ok case — impure function (success) should not be flagged
            new RuleCase(
            "ok-step-if-impure-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ contains(github.event.head_commit.message, 'skip') }}
                          run: echo ok
            """,
            []),
            // regression: trailing whitespace in bare constant should be trimmed in message text
            new RuleCase(
            "ng-step-if-constant-trailing-space",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: 'true '
                          run: echo ng
            """,
            ["constant expression \"true\" in condition. remove the if: section"]),
            // regression: leading whitespace in bare constant should be trimmed in message text
            new RuleCase(
            "ng-step-if-constant-leading-space",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ' false'
                          run: echo ng
            """,
            ["constant expression \"false\" in condition. remove the if: section"]),
            // regression: block scalar newline in constant should be trimmed in message text
            new RuleCase(
            "ng-step-if-constant-block-scalar",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - if: |\n          true\n        run: echo ng\n",
            ["constant expression \"true\" in condition. remove the if: section"]),
            // regression: snapshot.if constant should be detected
            new RuleCase(
            "ng-snapshot-if-constant",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    snapshot:
                        image-name: test
                        if: true
                    steps:
                        - run: echo ng
            """,
            ["constant expression \"true\" in condition. remove the if: section"]),
        };

        await AssertRuleCases(new IfCondRule(), "if-cond", cases);
    }

    [Test]
    public async Task RuleRegression_IfExprWrapperRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-already-wrapped",
            """
            on: push
            jobs:
                build:
                    if: ${{ github.ref != 'refs/heads/main' }}
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ success() }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-literal-true",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: true
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-literal-false",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: false
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-always-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: always()
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-failure-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: failure()
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-cancelled-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: cancelled()
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-success-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: success()
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-step-bare-comparison",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.event_name == 'push'
                          run: echo ng
            """,
            ["missing ${{ }} wrapper"]),
            new RuleCase(
            "ng-job-bare-comparison",
            """
            on: push
            jobs:
                build:
                    if: github.ref != 'refs/heads/main'
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["missing ${{ }} wrapper"]),
            new RuleCase(
            "ng-step-bare-context-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.event.pull_request.merged
                          run: echo ng
            """,
            ["missing ${{ }} wrapper"]),
            new RuleCase(
            "ng-step-bare-logical-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.event_name == 'push' && github.ref == 'refs/heads/main'
                          run: echo ng
            """,
            ["missing ${{ }} wrapper"]),
            new RuleCase(
            "ng-step-bare-negation",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: "!cancelled()"
                          run: echo ng
            """,
            ["missing ${{ }} wrapper"]),
            new RuleCase(
            "ng-snapshot-if-bare-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    snapshot:
                        image-name: test
                        if: github.event_name == 'push'
                    steps:
                        - run: echo ng
            """,
            ["missing ${{ }} wrapper"]),
        };

        await AssertRuleCases(new IfExprWrapperRule(), "if-expr-wrapper", cases);
    }

    [Test]
    public async Task RuleRegression_FakeTernaryRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-boolean-short-circuit",
            """
            on: push
            jobs:
                build:
                    if: ${{ (github.event_name == 'push' && success()) || failure() }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-if-fake-ternary",
            """
            on: push
            jobs:
                build:
                    if: ${{ github.ref_name == 'main' && 'prod' || 'dev' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["avoid fake ternary pattern 'cond && a || b'", "case expression"]),
            new RuleCase(
            "ng-step-if-fake-ternary",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.deploy && 'yes' || 'no' }}
                          run: echo ng
            """,
            ["avoid fake ternary pattern 'cond && a || b'", "explicit branching"]),
        };

        await AssertRuleCases(new FakeTernaryRule(), "fake-ternary", cases);
    }

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

    [Test]
    public async Task RuleRegression_InsecureCommandsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-unrelated-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        LOG_LEVEL: debug
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-env-unsecure-commands",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        ACTIONS_ALLOW_UNSECURE_COMMANDS: true
                    steps:
                        - run: echo ng
            """,
            ["ACTIONS_ALLOW_UNSECURE_COMMANDS", "migrate to environment files"]),
            new RuleCase(
            "ng-step-env-unsecure-commands",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            ACTIONS_ALLOW_UNSECURE_COMMANDS: "yes"
                          run: echo ng
            """,
            ["ACTIONS_ALLOW_UNSECURE_COMMANDS", "migrate to environment files"]),
        };

        await AssertRuleCases(new InsecureCommandsRule(), "insecure-commands", cases);
    }

    [Test]
    public async Task RuleRegression_OverprovisionedSecretsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-single-secret-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            TOKEN: ${{ secrets.GITHUB_TOKEN }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-two-step-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            TOKEN: ${{ secrets.GITHUB_TOKEN }}
                            API_KEY: ${{ secrets.API_KEY }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-multiple-step-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            TOKEN: ${{ secrets.GITHUB_TOKEN }}
                            API_KEY: ${{ secrets.API_KEY }}
                            SECRET_KEY: ${{ secrets.SECRET_KEY }}
                            PRIVATE_KEY: ${{ secrets.PRIVATE_KEY }}
                            APP_ID: ${{ secrets.APP_ID }}
                            DEPLOY_KEY: ${{ secrets.DEPLOY_KEY }}
                          run: echo ng
            """,
            ["more than 5 secret values", "minimum required"]),
            new RuleCase(
            "ok-five-job-secrets",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@v1
                    secrets:
                        token: ${{ secrets.GITHUB_TOKEN }}
                        api_key: ${{ secrets.API_KEY }}
                        secret_key: ${{ secrets.SECRET_KEY }}
                        private_key: ${{ secrets.PRIVATE_KEY }}
                        app_id: ${{ secrets.APP_ID }}
            """,
            []),
            new RuleCase(
            "ng-reusable-call-many-secrets",
            """
            on: push
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@v1
                    secrets:
                        token: ${{ secrets.GITHUB_TOKEN }}
                        api_key: ${{ secrets.API_KEY }}
                        secret_key: ${{ secrets.SECRET_KEY }}
                        private_key: ${{ secrets.PRIVATE_KEY }}
                        app_id: ${{ secrets.APP_ID }}
                        deploy_key: ${{ secrets.DEPLOY_KEY }}
            """,
            ["passes 6 explicit secrets", "minimum required secrets"]),
        };

        await AssertRuleCases(new OverprovisionedSecretsRule(), "overprovisioned-secrets", cases);
    }

    [Test]
    public async Task RuleRegression_ForbiddenUsesRule_TableDriven()
    {
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["forbidden-uses"] = new RuleConfig
                {
                    Allow = ["bad-org/safe-action"],
                    Deny = ["bad-org/*"],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-allowed-by-exception",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: bad-org/safe-action@v1
            """,
            []),
            new RuleCase(
            "ng-deny-policy-hit",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: bad-org/unsafe-action@v1
            """,
            ["denied by forbidden-uses policy", "bad-org/unsafe-action"]),
            new RuleCase(
            "ng-reusable-workflow-deny",
            """
            on: push
            jobs:
                reuse:
                    uses: bad-org/reusable/.github/workflows/reuse.yml@v1
            """,
            ["denied by forbidden-uses policy", "bad-org/reusable"]),
        };

        await AssertRuleCases(new ForbiddenUsesRule(), "forbidden-uses", cases, config);
    }

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

    [Test]
    public async Task RuleRegression_CredentialsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-no-host-image",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: node:20
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-public-registry-without-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/owner/app:latest
                    services:
                        cache:
                            image: docker.io/library/redis:7
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-additional-public-registries-without-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: registry.k8s.io/pause:3.10
                    services:
                        a:
                            image: quay.io/org/app:1
                        b:
                            image: mcr.microsoft.com/dotnet/runtime:8.0
                        c:
                            image: cgr.dev/chainguard/wolfi-base:latest
                        d:
                            image: nvcr.io/nvidia/pytorch:24.01-py3
                        e:
                            image: registry.access.redhat.com/ubi9/ubi:latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-private-registry-with-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: registry.example.com/team/app:1.0.0
                        credentials:
                            username: ${{ secrets.REG_USER }}
                            password: ${{ secrets.REG_PASS }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-container-private-without-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: registry.example.com/team/app:1.0.0
                    steps:
                        - run: echo ng
            """,
            ["credentials are not configured", "registry.example.com"]),
            new RuleCase(
            "ng-service-private-without-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        db:
                            image: private.example.org/team/db:15
                    steps:
                        - run: echo ng
            """,
            ["credentials are not configured", "private.example.org"]),
            new RuleCase(
            "ng-hardcoded-password-in-container",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: 'example.com/owner/image'
                        credentials:
                            username: user
                            password: pass
                    steps:
                        - run: echo ng
            """,
            ["\"password\" section in \"container\" section should be specified via secrets"]),
            new RuleCase(
            "ng-hardcoded-password-in-service",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        redis:
                            image: redis
                            credentials:
                                username: user
                                password: pass
                    steps:
                        - run: echo ng
            """,
            ["\"password\" section in \"redis\" service should be specified via secrets"]),
            new RuleCase(
            "ok-password-via-secrets-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: 'example.com/owner/image'
                        credentials:
                            username: ${{ secrets.REG_USER }}
                            password: ${{ secrets.REG_PASS }}
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new CredentialsRule(), "credentials", cases);
    }

    [Test]
    public async Task RuleRegression_TemplateInjectionRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-run-with-safe-expression",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref }}"
            """,
            []),
            new RuleCase(
            "ok-run-without-expression",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
            """,
            []),
            new RuleCase(
            "ng-run-uses-github-event-pull-request-title",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.pull_request.title }}"
            """,
            ["\"github.event.pull_request.title\" is potentially untrusted"]),
            new RuleCase(
            "ok-env-maps-github-event-comment-body",
            """
            on: issue_comment
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            COMMENT_BODY: ${{ github.event.comment.body }}
                          run: echo "$COMMENT_BODY"
            """,
            []),
            new RuleCase(
            "ng-run-uses-bracket-event-access",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github['event'].pull_request.title }}"
            """,
            ["\"github.event.pull_request.title\" is potentially untrusted"]),
            new RuleCase(
            "ok-run-uses-github-event-number-not-leaf",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.number }}"
            """,
            []),
            new RuleCase(
            "ng-run-uses-github-head-ref",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.head_ref }}"
            """,
            ["\"github.head_ref\" is potentially untrusted"]),
            new RuleCase(
            "ok-safe-function-contains-untrusted-input",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ contains(github.event.issue.title, 'bug') }}"
            """,
            []),
            new RuleCase(
            "ok-safe-function-startswith-untrusted-input",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ startsWith(github.event.pull_request.head.ref, 'feature/') }}"
            """,
            []),
            new RuleCase(
            "ng-unsafe-function-format-untrusted-input",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ format('{0}', github.event.issue.title) }}"
            """,
            ["\"github.event.issue.title\" is potentially untrusted"]),
            new RuleCase(
            "ng-github-script-with-untrusted-input",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/github-script@v7
                          with:
                            script: console.log('${{ github.event.head_commit.author.name }}')
            """,
            ["\"github.event.head_commit.author.name\" is potentially untrusted"]),
            new RuleCase(
            "ok-github-script-with-safe-expression",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/github-script@v7
                          with:
                            script: console.log('${{ github.ref }}')
            """,
            []),
            new RuleCase(
            "ok-action-input-not-github-script",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/stale@v9
                          with:
                            stale-pr-message: ${{ github.event.pull_request.title }} was closed
            """,
            []),
            new RuleCase(
            "ng-run-with-object-filter-untrusted",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ toJSON(github.event.*.body) }}'
            """,
            ["is potentially untrusted"]),
        };

        await AssertRuleCases(new TemplateInjectionRule(), "template-injection", cases);
    }

    [Test]
    public async Task RuleRegression_TemplateInjectionRule_PerReferenceReporting_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-single-untrusted-reference-names-path",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.head_commit.message }}"
            """,
            ["\"github.event.head_commit.message\" is potentially untrusted"]),
            new RuleCase(
            "ng-nested-untrusted-reports-all-three",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ github.event.pages[github.event.commits[github.event.issue.title].author.name].page_name }}
            """,
            [
                "\"github.event.pages.*.page_name\" is potentially untrusted",
                "\"github.event.commits.*.author.name\" is potentially untrusted",
                "\"github.event.issue.title\" is potentially untrusted",
            ]),
            new RuleCase(
            "ng-two-expressions-in-one-run",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.head_commit.message }}" and "${{ github.head_ref }}"
            """,
            [
                "\"github.event.head_commit.message\" is potentially untrusted",
                "\"github.head_ref\" is potentially untrusted",
            ]),
            new RuleCase(
            "ng-github-script-names-path",
            """
            on: issues
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/github-script@v7
                          with:
                            script: console.log('${{ github.event.head_commit.author.name }}')
            """,
            ["\"github.event.head_commit.author.name\" is potentially untrusted"]),
        };

        await AssertRuleCases(new TemplateInjectionRule(), "template-injection", cases);
    }

    [Test]
    public async Task RuleRegression_TemplateInjectionRule_PositionPrecision()
    {
        // actionlint expects 6:41 for: echo "Checking commit '${{ github.event.head_commit.message }}'"
        // Col 41 = start of "github" inside the expression body
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "Checking commit '${{ github.event.head_commit.message }}'"
            """);
        using var result = new LintEngine([new TemplateInjectionRule()]).Check(

            System.Text.Encoding.UTF8.GetBytes(yaml), "position-test.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "template-injection").ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).Contains("github.event.head_commit.message");

        // The untrusted reference starts at the "g" of "github" inside the expression
        var line6 = yaml.Split('\n')[5]; // 0-based index for line 6
        var expectedCol = line6.IndexOf("github.event.head_commit.message", StringComparison.Ordinal) + 1; // 1-based
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(6);
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(expectedCol);
    }

    [Test]
    public async Task RuleRegression_TemplateInjectionRule_NestedUntrustedPositions()
    {
        // actionlint expects 7:23, 7:42, 7:63 for nested untrusted references
        var yaml = NormalizeYaml("""
            name: Test
            on: pull_request
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ github.event.pages[github.event.commits[github.event.issue.title].author.name].page_name }}
            """);
        using var result = new LintEngine([new TemplateInjectionRule()]).Check(

            System.Text.Encoding.UTF8.GetBytes(yaml), "nested-test.yml");
        var diagnostics = result.Diagnostics
            .Where(x => x.RuleId == "template-injection")
            .OrderBy(x => x.Location.StartColumn)
            .ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(3);

        // All on line 7
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(7);
        await Assert.That(diagnostics[1].Location.StartLine).IsEqualTo(7);
        await Assert.That(diagnostics[2].Location.StartLine).IsEqualTo(7);

        // Check messages name correct paths
        await Assert.That(diagnostics[0].Message).Contains("github.event.pages.*.page_name");
        await Assert.That(diagnostics[1].Message).Contains("github.event.commits.*.author.name");
        await Assert.That(diagnostics[2].Message).Contains("github.event.issue.title");

        // Verify column positions
        var line7 = yaml.Split('\n')[6]; // 0-based for line 7
        var col1 = line7.IndexOf("github.event.pages[", StringComparison.Ordinal) + 1;
        var col2 = line7.IndexOf("github.event.commits[", StringComparison.Ordinal) + 1;
        var col3 = line7.IndexOf("github.event.issue.title", StringComparison.Ordinal) + 1;
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(col1);
        await Assert.That(diagnostics[1].Location.StartColumn).IsEqualTo(col2);
        await Assert.That(diagnostics[2].Location.StartColumn).IsEqualTo(col3);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-step-if-uses-steps-context",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: prep
                          run: echo ok
                        - if: ${{ steps.prep.outcome == 'success' }}
                          run: echo next
            """,
            []),
            new RuleCase(
            "ok-step-with-safe-context",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            repository: ${{ github.repository }}
            """,
            []),
            new RuleCase(
            "ng-job-if-uses-steps-context",
            """
            on: push
            jobs:
                build:
                    if: ${{ steps.prep.outcome == 'success' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["context \"steps\" is not allowed here"]),
            new RuleCase(
            "ng-job-if-uses-strategy-context",
            """
            on: push
            jobs:
                build:
                    if: ${{ strategy.fail-fast }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["context \"strategy\" is not allowed here"]),
            new RuleCase(
            "ng-job-if-uses-matrix-context",
            """
            on: push
            jobs:
                build:
                    if: ${{ matrix.os == 'ubuntu-latest' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["context \"matrix\" is not allowed here"]),
            new RuleCase(
            "ng-job-if-uses-secrets-context",
            """
            on: push
            jobs:
                build:
                    if: ${{ secrets.TOKEN != '' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["context \"secrets\" is not allowed here"]),
            new RuleCase(
            "ng-step-if-uses-secrets-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ secrets.TOKEN != '' }}
                          run: echo ng
            """,
            ["context \"secrets\" is not allowed here"]),
            new RuleCase(
            "ok-step-run-uses-secrets-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ secrets.TOKEN }}
            """,
            []),
            new RuleCase(
            "ok-step-env-uses-secrets-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            TOKEN: ${{ secrets.TOKEN }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-step-if-uses-unknown-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ foobar.value == 'x' }}
                          run: echo ng
            """,
            ["undefined context \"foobar\""]),
            new RuleCase(
            "ng-step-env-uses-unknown-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            DATA: ${{ unknown.payload }}
                          run: echo "$DATA"
            """,
            ["undefined context \"unknown\""]),
            new RuleCase(
            "ng-step-with-uses-unknown-context",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            repository: ${{ unknown.repository }}
            """,
            ["undefined context \"unknown\""]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_EnvKeyExpression_TableDriven()
    {
        var cases = new[]
        {
            // env key with valid runner property — should only get portability warning (from EnvVarRule, not here)
            new RuleCase(
            "ok-env-key-valid-runner-property",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hi
                          env:
                            ${{ runner.name }}: ''
            """,
            []),
            // env key with invalid runner property — should report property not defined
            new RuleCase(
            "ng-container-env-key-invalid-property",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: node:14.16
                        env:
                            ${{ runner.foooooo }}: ''
                    steps:
                        - run: echo hi
            """,
            ["property \"foooooo\" is not defined in \"runner\" context"]),
            // job env key with invalid runner property
            new RuleCase(
            "ng-job-env-key-invalid-property",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        ${{ runner.fooooooo }}: ''
                    steps:
                        - run: echo hi
            """,
            ["property \"fooooooo\" is not defined in \"runner\" context"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_InputDefaultTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            // ok: boolean default with boolean expression
            new RuleCase(
            "ok-bool-default-bool-expr",
            """
            on:
              workflow_call:
                inputs:
                  input1:
                    type: boolean
                  input2:
                    type: boolean
                    default: ${{ inputs.input1 }}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """,
            []),
            // ok: number default with number expression
            new RuleCase(
            "ok-number-default-number-expr",
            """
            on:
              workflow_call:
                inputs:
                  input1:
                    type: number
                  input2:
                    type: number
                    default: ${{ inputs.input1 }}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """,
            []),
            // ng: boolean input with string expression
            new RuleCase(
            "ng-bool-default-string-expr",
            """
            on:
              workflow_call:
                inputs:
                  input1:
                    type: string
                  input2:
                    type: boolean
                    default: ${{ inputs.input1 }}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ng
            """,
            ["type of input \"input2\" must be bool but found type string"]),
            // ng: number input with string expression
            new RuleCase(
            "ng-number-default-string-expr",
            """
            on:
              workflow_call:
                inputs:
                  input1:
                    type: string
                  input2:
                    type: number
                    default: ${{ inputs.input1 }}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ng
            """,
            ["type of input \"input2\" must be number but found type string"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ContextAvailability4C_TableDriven()
    {
        var cases = new[]
        {
            // 4.C-A: workflow_call output value should check root context availability
            new RuleCase(
            "ng-workflow-call-output-value-env-not-allowed",
            """
            on:
              workflow_call:
                outputs:
                  result:
                    value: ${{ env.FOO }}
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),

            new RuleCase(
            "ok-workflow-call-output-value-jobs-context",
            """
            on:
              workflow_call:
                outputs:
                  result:
                    value: ${{ jobs.build.outputs.foo }}
            jobs:
              build:
                runs-on: ubuntu-latest
                outputs:
                  foo: bar
                steps:
                  - run: echo ok
            """,
            []),

            // 4.C-B: snapshot.if should be checked for context availability
            new RuleCase(
            "ng-snapshot-if-env-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                snapshot:
                  image-name: my-image
                  if: ${{ env.FOO == 'foo' }}
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),

            new RuleCase(
            "ng-snapshot-if-runner-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                snapshot:
                  image-name: my-image
                  if: ${{ runner.name == 'foo' }}
                steps:
                  - run: echo ok
            """,
            ["context \"runner\" is not allowed here"]),

            new RuleCase(
            "ng-snapshot-if-secrets-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                snapshot:
                  image-name: my-image
                  if: ${{ secrets.FOO == 'foo' }}
                steps:
                  - run: echo ok
            """,
            ["context \"secrets\" is not allowed here"]),

            new RuleCase(
            "ok-snapshot-if-strategy-matrix-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    foo: [a, b]
                snapshot:
                  image-name: my-image
                  if: ${{ matrix.foo == 'a' && strategy.fail-fast }}
                steps:
                  - run: echo ok
            """,
            []),

            // 4.C-C: service entrypoint/command should be checked for context availability
            new RuleCase(
            "ng-service-entrypoint-env-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                services:
                  nginx:
                    image: nginx
                    entrypoint: ${{ env.FOO }}
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),

            new RuleCase(
            "ng-service-command-env-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                services:
                  nginx:
                    image: nginx
                    command: ${{ env.FOO }}
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),

            new RuleCase(
            "ok-service-entrypoint-github-context",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                services:
                  nginx:
                    image: nginx
                    entrypoint: ${{ github.actor }}
                steps:
                  - run: echo ok
            """,
            []),

            // Services expression form: env context should not be allowed
            new RuleCase(
            "ng-services-expression-env-not-allowed",
            """
            on:
              workflow_call:
                inputs:
                  bool:
                    type: boolean
            jobs:
              build:
                runs-on: ubuntu-latest
                services: ${{ inputs.bool || env.FOO }}
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_DynamicContext_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-step-accesses-known-step-id",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: prep
                          run: echo ok
                        - if: ${{ steps.prep.outcome == 'success' }}
                          run: echo next
            """,
            []),
            new RuleCase(
            "ok-step-accesses-known-matrix-key",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                    steps:
                        - run: echo ${{ matrix.os }}
            """,
            []),
            new RuleCase(
            "ok-step-accesses-known-needs-job",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo build
                test:
                    runs-on: ubuntu-latest
                    needs: [build]
                    steps:
                        - run: echo ${{ needs.build.result }}
            """,
            []),
            new RuleCase(
            "ok-step-accesses-known-workflow-call-input",
            """
            on:
                workflow_call:
                    inputs:
                        environment:
                            type: string
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ inputs.environment }}
            """,
            []),
            new RuleCase(
            "ok-matrix-no-rows-loose-object-no-error",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            include:
                                - os: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.os }}
            """,
            []),
            new RuleCase(
            "ng-step-accesses-unknown-step-id",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: prep
                          run: echo ok
                        - if: ${{ steps.nonexistent.outcome == 'success' }}
                          run: echo next
            """,
            ["\"nonexistent\" is not defined in \"steps\" context"]),
            new RuleCase(
            "ng-step-accesses-unknown-matrix-key",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                    steps:
                        - env:
                            VALUE: ${{ matrix.unknown_key }}
                          run: echo "$VALUE"
            """,
            ["\"unknown_key\" is not defined in \"matrix\" context"]),
            new RuleCase(
            "ng-step-accesses-unknown-needs-job",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo build
                test:
                    runs-on: ubuntu-latest
                    needs: [build]
                    steps:
                        - env:
                            RESULT: ${{ needs.nonexistent.outputs.foo }}
                          run: echo "$RESULT"
            """,
            ["\"nonexistent\" is not defined in \"needs\" context"]),
            new RuleCase(
            "ng-step-accesses-unknown-workflow-call-input",
            """
            on:
                workflow_call:
                    inputs:
                        environment:
                            type: string
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ inputs.unknown_param }}
                          run: echo "$VAL"
            """,
            ["\"unknown_param\" is not defined in \"inputs\" context"]),
            // index access: inputs['unknown'] should be flagged the same as inputs.unknown
            new RuleCase(
            "ng-index-access-unknown-input",
            """
            on:
                workflow_call:
                    inputs:
                        environment:
                            type: string
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ inputs['unknown_param'] }}
                          run: echo "$VAL"
            """,
            ["\"unknown_param\" is not defined in \"inputs\" context"]),
            // index access: inputs['environment'] should pass
            new RuleCase(
            "ok-index-access-known-input",
            """
            on:
                workflow_call:
                    inputs:
                        environment:
                            type: string
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ inputs['environment'] }}
                          run: echo "$VAL"
            """,
            []),
            // regression: matrix include-only axis keys should be accessible
            new RuleCase(
            "ok-matrix-include-only-axis-accessible",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                            node: [14, 15]
                            include:
                                - node: 15
                                  npm: 7.5.4
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo ${{ matrix.os }}
                        - run: echo ${{ matrix.node }}
                        - run: echo ${{ matrix.npm }}
            """,
            []),
            // regression: include-only matrix (no row axes) should resolve keys
            new RuleCase(
            "ok-matrix-include-only-no-rows",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            include:
                                - os: ubuntu-latest
                                  version: 1
                                - os: windows-latest
                                  version: 2
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo ${{ matrix.version }}
            """,
            []),
            // regression: step env with expression scalar should not error
            new RuleCase(
            "ok-step-env-expression-scalar",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            env_object:
                                - FOO: BAR
                                - FOO: PIYO
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "$FOO"
                          env: ${{ matrix.env_object }}
            """,
            []),
            // A-3: matrix nested object property access — known property should be fine
            new RuleCase(
            "ok-matrix-nested-object-property",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            package:
                                - name: 'foo'
                                  optional: true
                                - name: 'bar'
                                  optional: false
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.package.name }}
            """,
            []),
            // A-3: matrix nested object — unknown property should error
            new RuleCase(
            "ng-matrix-nested-object-unknown-property",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            package:
                                - name: 'foo'
                                  optional: true
                                - name: 'bar'
                                  optional: false
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.package.dev }}
            """,
            ["\"dev\" is not defined"]),
            // A-3: matrix undefined axis (no such key at all)
            new RuleCase(
            "ng-matrix-undefined-axis",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo ${{ matrix.platform }}
            """,
            ["\"platform\" is not defined in \"matrix\" context"]),
            // A-3: empty matrix in other job — matrix should be strict empty
            new RuleCase(
            "ng-matrix-empty-in-other-job",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo test
                other:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.os }}
            """,
            ["\"os\" is not defined in \"matrix\" context"]),
            // A-19: popular action output — known output should be fine
            new RuleCase(
            "ok-popular-action-known-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          id: cache
                          with:
                            key: ${{ hashFiles('**/*.lock') }}
                            path: ./packages
                        - run: echo ${{ steps.cache.outputs.cache-hit }}
            """,
            []),
            // A-19: popular action output — typo should be flagged
            new RuleCase(
            "ng-popular-action-unknown-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          id: cache
                          with:
                            key: ${{ hashFiles('**/*.lock') }}
                            path: ./packages
                        - run: echo ${{ steps.cache.outputs.cache_hit }}
            """,
            ["\"cache_hit\" is not defined"]),
            // regression: github.event.inputs.unknown should be flagged for workflow_dispatch
            new RuleCase(
            "ng-github-event-inputs-unknown-property",
            """
            on:
              workflow_dispatch:
                inputs:
                  myinput:
                    type: string
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo "${{ github.event.inputs.select }}"
            """,
            ["\"select\" is not defined"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ComparisonTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-bool-input-greater-than-number",
            """
            on:
                workflow_call:
                    inputs:
                        timeout:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.timeout > 60 }}
                          run: echo timeout
            """,
            ["bool value cannot be compared to number value with '>' operator"]),
            new RuleCase(
            "ok-number-input-less-than-number",
            """
            on:
                workflow_call:
                    inputs:
                        count:
                            type: number
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.count < 100 }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-string-input-equals-string",
            """
            on:
                workflow_call:
                    inputs:
                        env:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.env == 'production' }}
                          run: echo deploy
            """,
            []),
            new RuleCase(
            "ng-bool-input-less-or-equal-number",
            """
            on:
                workflow_call:
                    inputs:
                        verbose:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.verbose <= 5 }}
                          run: echo ok
            """,
            ["bool value cannot be compared to number value with '<=' operator"]),
            new RuleCase(
            "ng-bool-input-greater-or-equal-number",
            """
            on:
                workflow_call:
                    inputs:
                        flag:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.flag >= 1 }}
                          run: echo ok
            """,
            ["bool value cannot be compared to number value with '>=' operator"]),
            new RuleCase(
            "ng-bool-input-not-equals-number",
            """
            on:
                workflow_call:
                    inputs:
                        flag:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.flag != 60 }}
                          run: echo ok
            """,
            ["bool value cannot be compared to number value with '!=' operator"]),
            new RuleCase(
            "ok-string-input-not-equals-string",
            """
            on:
                workflow_call:
                    inputs:
                        env:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.env != 'staging' }}
                          run: echo deploy
            """,
            []),
            new RuleCase(
            "ok-any-input-greater-than-number",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ github.event.number > 0 }}
                          run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_TemplateTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-step-env-object-in-template",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ fromJson('{"a":1}') }}
                          run: echo "$VAL"
            """,
            ["{a: number} value in ${{ }}"]),
            new RuleCase(
            "ng-step-env-null-in-template",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ null }}
                          run: echo "$VAL"
            """,
            ["null value in ${{ }}"]),
            new RuleCase(
            "ok-step-if-object-no-template-warning",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ fromJson('{"a":1}') }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-step-env-string-in-template",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ github.ref }}
                          run: echo "$VAL"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_MatrixArrayTemplateTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-matrix-array-in-template",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            bar:
                                - [42]
                                - [true]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.bar }}
            """,
            ["array value in ${{ }}"]),
            new RuleCase(
            "ok-matrix-array-element-access",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            bar:
                                - [42]
                                - [true]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.bar[0] }}
            """,
            []),
            new RuleCase(
            "ok-matrix-mixed-types-any",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            foo:
                                - 'string value'
                                - 42
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.foo }}
            """,
            []),
            new RuleCase(
            "ng-matrix-object-in-template",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            obj:
                                - { a: 1, b: 2 }
                                - { a: 3, b: 4 }
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.obj }}
            """,
            ["{a: number; b: number} value in ${{ }}"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_EnvMappingTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-env-string-as-mapping",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            env_string:
                                - 'FOO=BAR'
                                - 'FOO=PIYO'
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "$FOO"
                          env: ${{ matrix.env_string }}
            """,
            ["cannot be expanded as mapping"]),
            new RuleCase(
            "ok-env-object-as-mapping",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            env_object:
                                - FOO: BAR
                                - FOO: PIYO
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "$FOO"
                          env: ${{ matrix.env_object }}
            """,
            []),
            new RuleCase(
            "ok-env-any-as-mapping",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "$FOO"
                          env: ${{ fromJson('{"FOO":"bar"}') }}
            """,
            []),
            new RuleCase(
            "ng-env-array-as-mapping",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            arr:
                                - [1, 2]
                                - [3, 4]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo test
                          env: ${{ matrix.arr }}
            """,
            ["cannot be expanded as mapping"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_CredentialsObjectTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-credentials-fromjson-object",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    container:
                        image: ubuntu:latest
                        credentials: ${{ fromJSON('{}') }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-credentials-string-expression",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    container:
                        image: ubuntu:latest
                        credentials: ${{ 'username:password' }}
                    steps:
                        - run: echo
            """,
            ["type of expression at \"credentials\" must be object but found type string"]),
            new RuleCase(
            "ng-services-string-expression",
            """
            on: push
            jobs:
                test:
                    services: ${{ 'redis' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["type of expression at \"services\" must be object but found type string"]),
            new RuleCase(
            "ok-services-fromjson-object",
            """
            on: push
            jobs:
                test:
                    services: ${{ fromJSON('{}') }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-service-credentials-string-expression",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    services:
                        redis:
                            image: redis:latest
                            credentials: ${{ 'user:pass' }}
                    steps:
                        - run: echo
            """,
            ["type of expression at \"credentials\" must be object but found type string"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_IndexTypeCheckWithOverrides_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-bool-index-on-object",
            """
            on:
                workflow_dispatch:
                    inputs:
                        verbose:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ env[inputs.verbose] }}"
            """,
            ["index of object must be string, but got bool"]),
            new RuleCase(
            "ng-number-index-on-object",
            """
            on:
                workflow_dispatch:
                    inputs:
                        age:
                            type: number
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ env[inputs.age] }}"
            """,
            ["index of object must be string, but got number"]),
            new RuleCase(
            "ok-string-index-on-object",
            """
            on:
                workflow_dispatch:
                    inputs:
                        name:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ env[inputs.name] }}"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_SecretsResolution_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-workflow-call-secret-known",
            """
            on:
                workflow_call:
                    secrets:
                        DEPLOY_KEY:
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            KEY: ${{ secrets.DEPLOY_KEY }}
                          run: echo "$KEY"
            """,
            []),
            new RuleCase(
            "ng-workflow-call-secret-unknown",
            """
            on:
                workflow_call:
                    secrets:
                        DEPLOY_KEY:
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            KEY: ${{ secrets.UNKNOWN_SECRET }}
                          run: echo "$KEY"
            """,
            ["\"UNKNOWN_SECRET\" is not defined in \"secrets\" context"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_NeedsOutputValidation_TableDriven()
    {
        var cases = new[]
        {
            // #8: needs.build.outputs.built should be detected as undefined when build has no such output
            new RuleCase(
            "ng-needs-unknown-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        image_tag: ${{ steps.build.outputs.tag }}
                    steps:
                        - id: build
                          run: echo "tag=v1" >> $GITHUB_OUTPUT
                test:
                    runs-on: ubuntu-latest
                    needs: [build]
                    steps:
                        - env:
                            TAG: ${{ needs.build.outputs.typo_output }}
                          run: echo "$TAG"
            """,
            ["\"typo_output\" is not defined in \"needs\" context"]),
            // #8: needs.build.outputs.image_tag should be valid
            new RuleCase(
            "ok-needs-known-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        image_tag: ${{ steps.build.outputs.tag }}
                    steps:
                        - id: build
                          run: echo "tag=v1" >> $GITHUB_OUTPUT
                test:
                    runs-on: ubuntu-latest
                    needs: [build]
                    steps:
                        - env:
                            TAG: ${{ needs.build.outputs.image_tag }}
                          run: echo "$TAG"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ReusableWorkflowCallNeedsOutputs_TableDriven()
    {
        var cases = new[]
        {
            // Reusable workflow call jobs don't declare outputs locally — their outputs come from
            // the called workflow. The linter cannot determine the available outputs without
            // fetching the remote workflow, so needs.<reusable-job>.outputs.* must be treated as
            // loose (no false positive).
            new RuleCase(
            "ok-reusable-workflow-call-needs-outputs",
            """
            on: push
            jobs:
                new-version:
                    uses: owner/repo/.github/workflows/reusable.yml@main
                    with:
                        ref: main
                deploy:
                    runs-on: ubuntu-latest
                    needs: [new-version]
                    steps:
                        - env:
                            TAG: ${{ needs.new-version.outputs.version }}
                          run: echo "$TAG"
            """,
            []),
            // Local reusable workflow call — needs.<reusable-job>.outputs.* is only treated as
            // loose when the referenced workflow cannot be resolved locally. If it can be
            // resolved and defines on.workflow_call.outputs, validation is strict.
            new RuleCase(
            "ok-local-reusable-workflow-call-needs-outputs",
            """
            on: push
            jobs:
                new-version:
                    uses: ./.github/workflows/reusable.yml
                deploy:
                    runs-on: ubuntu-latest
                    needs: [new-version]
                    steps:
                        - env:
                            TAG: ${{ needs.new-version.outputs.version }}
                          run: echo "$TAG"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_LocalReusableWorkflowOutputResolution()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-reusable-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var reusablePath = Path.Combine(workflowsDir, "reusable.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            // Reusable workflow declares one output: "version"
            var reusableYaml = """
            on:
              workflow_call:
                outputs:
                  version:
                    description: The computed version
                    value: ${{ jobs.compute.outputs.ver }}
            jobs:
              compute:
                runs-on: ubuntu-latest
                outputs:
                  ver: ${{ steps.v.outputs.ver }}
                steps:
                  - id: v
                    run: echo "ver=1.0.0" >> "$GITHUB_OUTPUT"
            """;

            // Case 1: ng — references undefined output "typo_output"
            var callerYamlNg = """
            on: push
            jobs:
              new-version:
                uses: ./.github/workflows/reusable.yml
              deploy:
                runs-on: ubuntu-latest
                needs: [new-version]
                steps:
                  - env:
                      TAG: ${{ needs.new-version.outputs.typo_output }}
                    run: echo "$TAG"
            """;

            // Case 2: ok — references valid output "version"
            var callerYamlOk = """
            on: push
            jobs:
              new-version:
                uses: ./.github/workflows/reusable.yml
              deploy:
                runs-on: ubuntu-latest
                needs: [new-version]
                steps:
                  - env:
                      TAG: ${{ needs.new-version.outputs.version }}
                    run: echo "$TAG"
            """;

            File.WriteAllText(reusablePath, NormalizeYaml(reusableYaml), Encoding.UTF8);

            // Test ng case
            File.WriteAllText(callerPath, NormalizeYaml(callerYamlNg), Encoding.UTF8);
            using var resultNg = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);
            var msgsNg = resultNg.Diagnostics.Where(x => x.RuleId == "expr-undefined-var").Select(x => x.Message).ToArray();
            await Assert.That(msgsNg.Any(m => m.Contains("\"typo_output\" is not defined", StringComparison.Ordinal))).IsTrue();

            // Test ok case
            File.WriteAllText(callerPath, NormalizeYaml(callerYamlOk), Encoding.UTF8);
            using var resultOk = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);
            var msgsOk = resultOk.Diagnostics.Where(x => x.RuleId == "expr-undefined-var").Select(x => x.Message).ToArray();
            await Assert.That(msgsOk.Any(m => m.Contains("is not defined", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_LocalReusableWorkflowNoOutputs()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-reusable-noout-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var reusablePath = Path.Combine(workflowsDir, "reusable-no-outputs.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            // Reusable workflow with workflow_call but NO outputs declared
            var reusableYaml = """
            on:
              workflow_call:
                inputs:
                  ref:
                    type: string
            jobs:
              work:
                runs-on: ubuntu-latest
                steps:
                  - run: echo "working"
            """;

            // Caller references an output that doesn't exist — should be flagged
            var callerYaml = """
            on: push
            jobs:
              compute:
                uses: ./.github/workflows/reusable-no-outputs.yml
              deploy:
                runs-on: ubuntu-latest
                needs: [compute]
                steps:
                  - env:
                      X: ${{ needs.compute.outputs.something }}
                    run: echo "$X"
            """;

            File.WriteAllText(reusablePath, NormalizeYaml(reusableYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);
            var msgs = result.Diagnostics.Where(x => x.RuleId == "expr-undefined-var").Select(x => x.Message).ToArray();
            // The called workflow declares no outputs, so needs.compute.outputs.something should be flagged
            await Assert.That(msgs.Any(m => m.Contains("is not defined", StringComparison.Ordinal) || m.Contains("no properties are defined", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_NeedsUndefinedJob_TableDriven()
    {
        var cases = new[]
        {
            // A-4: needs.prepare undefined when not in needs list
            new RuleCase(
            "ng-needs-job-not-in-needs-list",
            """
            on: push
            jobs:
                prepare:
                    runs-on: ubuntu-latest
                    outputs:
                        prepared: ${{ steps.a.outputs.val }}
                    steps:
                        - id: a
                          run: echo "val=1" >> $GITHUB_OUTPUT
                        - run: echo '${{ needs.prepare.outputs.prepared }}'
            """,
            ["\"prepare\" is not defined in \"needs\" context"]),
            // A-4: needs.some_job undefined (job doesn't exist)
            new RuleCase(
            "ng-needs-nonexistent-job",
            """
            on: push
            jobs:
                install:
                    runs-on: ubuntu-latest
                    outputs:
                        installed: ok
                    steps:
                        - run: echo install
                build:
                    needs: [install]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ needs.some_job }}'
            """,
            ["\"some_job\" is not defined in \"needs\" context"]),
            // A-4: needs.build undefined in other job (build not in other's needs)
            new RuleCase(
            "ng-needs-job-not-declared-in-needs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        built: ok
                    steps:
                        - run: echo build
                other:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ needs.build.outputs.built }}'
            """,
            ["\"build\" is not defined in \"needs\" context"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_StepsCrossJob_TableDriven()
    {
        var cases = new[]
        {
            // A-5: steps.get_value undefined in other job (step IDs are job-local)
            new RuleCase(
            "ng-steps-cross-job-reference",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - id: get_value
                          run: echo "name=foo" >> $GITHUB_OUTPUT
                        - run: echo '${{ steps.get_value.outputs.name }}'
                other:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ steps.get_value.outputs.name }}'
            """,
            ["\"get_value\" is not defined in \"steps\" context"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_StepsOrderValidation_TableDriven()
    {
        var cases = new[]
        {
            // #9: referencing a step ID that hasn't been defined yet should be an error
            new RuleCase(
            "ng-step-reference-before-definition",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ steps.later.outcome == 'success' }}
                          run: echo "first"
                        - id: later
                          run: echo "later"
            """,
            ["\"later\" is not defined in \"steps\" context"]),
            // #9: referencing a step ID that was defined earlier is fine
            new RuleCase(
            "ok-step-reference-after-definition",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: earlier
                          run: echo "earlier"
                        - if: ${{ steps.earlier.outcome == 'success' }}
                          run: echo "second"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_RunnerContextInMatrix_TableDriven()
    {
        var cases = new[]
        {
            // #23: runner context should NOT be available in strategy.matrix expressions
            // (currently Job scope doesn't include runner, so this may already pass)
            new RuleCase(
            "ng-matrix-uses-runner-context",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                    runs-on: ${{ matrix.os }}
                    steps:
                        - if: ${{ runner.os == 'Linux' }}
                          run: echo ok
            """,
            []),
            // runner context IS valid in step scope — should not error
            new RuleCase(
            "ok-step-uses-runner-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ runner.os == 'Linux' }}
                          run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ReusableWorkflowOutputs_TableDriven()
    {
        var cases = new[]
        {
            // #25: jobs.<id>.outputs.<name> in workflow_call output value should validate
            new RuleCase(
            "ng-workflow-output-references-unknown-job-output",
            """
            on:
                workflow_call:
                    outputs:
                        image:
                            value: ${{ jobs.build.outputs.imagetag }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        image_tag: ${{ steps.b.outputs.tag }}
                    steps:
                        - id: b
                          run: echo "tag=v1" >> $GITHUB_OUTPUT
            """,
            ["\"imagetag\" is not defined"]),
            // #25: correct output name should not error
            new RuleCase(
            "ok-workflow-output-references-known-job-output",
            """
            on:
                workflow_call:
                    outputs:
                        image:
                            value: ${{ jobs.build.outputs.image_tag }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        image_tag: ${{ steps.b.outputs.tag }}
                    steps:
                        - id: b
                          run: echo "tag=v1" >> $GITHUB_OUTPUT
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_RunAndWithExpressions_TableDriven()
    {
        var cases = new[]
        {
            // A-4: run field expression uses unknown context
            new RuleCase(
            "ng-run-field-unknown-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ bogus.value }}
            """,
            ["undefined context \"bogus\""]),
            // A-4: run field expression uses matrix key from wrong job
            new RuleCase(
            "ng-run-field-matrix-key-from-wrong-job",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo build
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.os }}
            """,
            ["\"os\" is not defined in \"matrix\" context"]),
            // A-5: action with input expression using unknown context
            new RuleCase(
            "ng-action-with-input-unknown-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            ref: ${{ nosuch.branch }}
            """,
            ["undefined context \"nosuch\""]),
            // A-4/A-5: run and with expressions using valid context should not error
            new RuleCase(
            "ok-run-and-with-valid-contexts",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            ref: ${{ github.ref }}
                        - run: echo ${{ github.sha }}
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ContextAvailability_WorkflowLevel_TableDriven()
    {
        var cases = new[]
        {
            // run-name: env context not allowed
            new RuleCase(
            "ng-run-name-env",
            """
            run-name: ${{ env.FOO }}
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // workflow env: env context not allowed (self-reference)
            new RuleCase(
            "ng-workflow-env-self-ref",
            """
            on: push
            env:
                BAR: ${{ env.BAR }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // workflow concurrency: env context not allowed
            new RuleCase(
            "ng-workflow-concurrency-env",
            """
            on: push
            concurrency:
                group: ${{ env.FOO }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // workflow_call input default: env context not allowed
            new RuleCase(
            "ng-workflow-call-input-default-env",
            """
            on:
                workflow_call:
                    inputs:
                        foo:
                            type: string
                            default: ${{ env.FOO }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // OK: workflow env using github and secrets
            new RuleCase(
            "ok-workflow-env-github-secrets",
            """
            on: push
            env:
                FOO: ${{ github.sha }}
                BAR: ${{ secrets.TOKEN }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ContextAvailability_JobLevel_TableDriven()
    {
        var cases = new[]
        {
            // job.name: runner not allowed
            new RuleCase(
            "ng-job-name-runner",
            """
            on: push
            jobs:
                build:
                    name: ${{ runner.name }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.runs-on: env and runner not allowed
            new RuleCase(
            "ng-job-runs-on-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ env.SUFFIX }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            new RuleCase(
            "ng-job-runs-on-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ runner.OS }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.concurrency: env not allowed
            new RuleCase(
            "ng-job-concurrency-env",
            """
            on: push
            jobs:
                build:
                    concurrency:
                        group: ${{ env.FOO }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.container.credentials: runner not allowed
            new RuleCase(
            "ng-job-container-credentials-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: node:14
                        credentials:
                            username: ${{ runner.os }}
                            password: ${{ env.FOO }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.continue-on-error: env not allowed
            new RuleCase(
            "ng-job-continue-on-error-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    continue-on-error: ${{ env.FOO == '' }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.environment: runner not allowed
            new RuleCase(
            "ng-job-environment-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    environment:
                        name: ${{ runner.name }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.strategy: env not allowed
            new RuleCase(
            "ng-job-strategy-env",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os:
                                - ${{ env.OS }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.timeout-minutes: env not allowed
            new RuleCase(
            "ng-job-timeout-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    timeout-minutes: ${{ env.TIMEOUT }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.outputs: OK (env, runner, steps all allowed)
            new RuleCase(
            "ok-job-outputs-env-runner-steps",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        foo: ${{ runner.name }}-${{ env.FOO }}-${{ steps.s1.outputs.x }}
                    steps:
                        - id: s1
                          run: echo
            """,
            []),
            // job.defaults.run: env allowed, runner not allowed
            new RuleCase(
            "ng-job-defaults-run-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    defaults:
                        run:
                            working-directory: ${{ runner.temp }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.services.image: env not allowed
            new RuleCase(
            "ng-job-services-image-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        nginx:
                            image: ${{ env.IMAGE }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.services.credentials: runner not allowed
            new RuleCase(
            "ng-job-services-credentials-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        nginx:
                            image: nginx
                            credentials:
                                username: ${{ runner.name }}
                                password: ${{ env.PASSWORD }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.secrets: OK (secrets allowed for reusable workflow calls)
            new RuleCase(
            "ok-job-secrets-secrets",
            """
            on: push
            jobs:
                caller:
                    uses: owner/repo/workflow.yml@main
                    secrets:
                        password: ${{ secrets.PASSWORD }}
            """,
            []),
            // job.with (reusable): env not allowed
            new RuleCase(
            "ng-job-with-env",
            """
            on: push
            jobs:
                caller:
                    uses: owner/repo/workflow.yml@main
                    with:
                        some-input: ${{ env.HELLO }}
            """,
            ["context \"env\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ContextAvailability_StepLevel_TableDriven()
    {
        var cases = new[]
        {
            // step.name: OK (all step contexts available)
            new RuleCase(
            "ok-step-name-env-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - name: ${{ env.VERSION }} on ${{ runner.name }}
                          run: echo
            """,
            []),
            // step.continue-on-error: OK (inputs allowed)
            new RuleCase(
            "ok-step-continue-on-error-inputs",
            """
            on:
                workflow_call:
                    inputs:
                        bool:
                            type: boolean
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - continue-on-error: ${{ inputs.bool }}
                          run: echo
            """,
            []),
            // step.timeout-minutes: OK
            new RuleCase(
            "ok-step-timeout-minutes-inputs",
            """
            on:
                workflow_call:
                    inputs:
                        timeout:
                            type: number
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - timeout-minutes: ${{ inputs.timeout }}
                          run: echo
            """,
            []),
            // step.working-directory: OK (runner allowed at step level)
            new RuleCase(
            "ok-step-working-directory-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - working-directory: ${{ runner.temp }}
                          run: echo
            """,
            []),
            // step.if: secrets not allowed
            new RuleCase(
            "ng-step-if-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ secrets.PASSWORD != '' }}
                          run: echo
            """,
            ["context \"secrets\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_EnvContextBanned_TableDriven()
    {
        var cases = new[]
        {
            // workflow env cannot reference env context
            new RuleCase(
            "ng-workflow-env-env-context",
            """
            on: push
            env:
                ERROR1: ${{ env.PATH }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job env cannot reference env context
            new RuleCase(
            "ng-job-env-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        ERROR2: ${{ env.PATH }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // step env CAN reference env context (OK)
            new RuleCase(
            "ok-step-env-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
                          env:
                            BAR: ${{ env.FOO }}
            """,
            []),
            // container env CAN reference env context (OK)
            new RuleCase(
            "ok-container-env-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: node:14
                        env:
                            MYPATH: ${{ env.PATH }}
                    steps:
                        - run: echo
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_JobIfEnvBanned_TableDriven()
    {
        var cases = new[]
        {
            // job.if with env context: not allowed
            new RuleCase(
            "ng-job-if-env-dollar-brace",
            """
            on: push
            jobs:
                test1:
                    runs-on: ubuntu-latest
                    if: ${{ env.FOO == 'aaa' }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.if without ${{ }}: env not allowed
            new RuleCase(
            "ng-job-if-env-bare",
            """
            on: push
            jobs:
                test2:
                    runs-on: ubuntu-latest
                    if: env.FOO == 'aaa'
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // reusable workflow call job if: env not allowed
            new RuleCase(
            "ng-reusable-job-if-env",
            """
            on: push
            jobs:
                test3:
                    uses: org/repo/workflow.yml@v1
                    if: ${{ env.FOO == 'aaa' }}
            """,
            ["context \"env\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ShellKeyContextAvailability_TableDriven()
    {
        var cases = new[]
        {
            // workflow-level defaults.run.shell: no context available
            new RuleCase(
            "ng-workflow-defaults-shell-env",
            """
            on: push
            defaults:
                run:
                    shell: ${{ env.SHELL }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job-level defaults.run.shell: env IS available (OK)
            new RuleCase(
            "ok-job-defaults-shell-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    defaults:
                        run:
                            shell: ${{ env.SHELL }}
                    steps:
                        - run: echo
            """,
            []),
            // step-level shell: no context available
            new RuleCase(
            "ng-step-shell-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
                          shell: ${{ env.SHELL }}
            """,
            ["context \"env\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_SpecialFunctionAvailability_TableDriven()
    {
        var cases = new[]
        {
            // status functions OK in job.if
            new RuleCase(
            "ok-always-in-job-if",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: always()
                    steps:
                        - run: echo
            """,
            []),
            new RuleCase(
            "ok-failure-in-step-if",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: failure()
                          run: echo
            """,
            []),
            // status functions NOT OK in strategy.matrix
            new RuleCase(
            "ng-always-in-strategy-matrix",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            errors:
                                - ${{ always() }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["function \"always\" is not allowed here"]),
            // hashFiles OK in step level
            new RuleCase(
            "ok-hashfiles-in-step-run",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ hashFiles('...') }}"
            """,
            []),
            // hashFiles NOT OK in job.if
            new RuleCase(
            "ng-hashfiles-in-job-if",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: ${{ hashFiles('...') }}
                    steps:
                        - run: echo
            """,
            ["function \"hashFiles\" is not allowed here"]),
            // success() NOT OK in step.run
            new RuleCase(
            "ng-success-in-step-run",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo 'success? ${{ success() }}'
            """,
            ["function \"success\" is not allowed here"]),
            // hashFiles NOT OK in strategy.matrix
            new RuleCase(
            "ng-hashfiles-in-strategy-matrix",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            errors:
                                - ${{ hashFiles('...') }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["function \"hashFiles\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_StepIdNoContext_TableDriven()
    {
        var cases = new[]
        {
            // step.id: no context allowed
            new RuleCase(
            "ng-step-id-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: ${{ inputs.foo }}
                          run: echo
            """,
            ["context \"inputs\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_MessageIncludesAvailableContexts_TableDriven()
    {
        var cases = new[]
        {
            // Error message should list available contexts
            new RuleCase(
            "ng-job-if-env-lists-available-contexts",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    if: ${{ env.FOO == 'aaa' }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here", "available contexts are"]),
            // "no context is available here" for shell
            new RuleCase(
            "ng-workflow-shell-no-context",
            """
            on: push
            defaults:
                run:
                    shell: ${{ env.SHELL }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here", "no context is available here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_InputsWithoutWorkflowCall_TableDriven()
    {
        var cases = new[]
        {
            // When no workflow_call event, inputs has no properties → inputs.some_input is undefined
            new RuleCase(
            "ng-inputs-without-workflow-call",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ inputs.some_input }}
            """,
            ["property \"some_input\" is not defined in \"inputs\" context"]),
            // With workflow_call + defined input → OK
            new RuleCase(
            "ok-inputs-with-workflow-call",
            """
            on:
                workflow_call:
                    inputs:
                        my_input:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ inputs.my_input }}
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_WorkflowCallOutputsSema_TableDriven()
    {
        var cases = new[]
        {
            // job0 has no outputs → jobs.job0.outputs.some_output is undefined
            new RuleCase(
            "ng-workflow-call-output-no-job-outputs",
            """
            on:
                workflow_call:
                    outputs:
                        output1:
                            value: ${{ jobs.job0.outputs.some_output }}
            jobs:
                job0:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hi
            """,
            ["property \"some_output\" is not defined"]),
            // job1 has outputs but unknown_output is not among them
            new RuleCase(
            "ng-workflow-call-output-unknown-property",
            """
            on:
                workflow_call:
                    outputs:
                        output2:
                            value: ${{ jobs.job1.outputs.unknown_output }}
            jobs:
                job1:
                    runs-on: ubuntu-latest
                    outputs:
                        foo: bar
                    steps:
                        - run: echo hello
            """,
            ["property \"unknown_output\" is not defined"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_InputDefaultForwardReference_TableDriven()
    {
        var cases = new[]
        {
            // input2 not yet defined when input1.default references it
            new RuleCase(
            "ng-input-default-forward-ref",
            """
            on:
                workflow_call:
                    inputs:
                        input1:
                            type: string
                            default: ${{ inputs.input2 }}
                        input2:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["property \"input2\" is not defined in \"inputs\" context"]),
            // input3 references itself — not yet defined
            new RuleCase(
            "ng-input-default-self-ref",
            """
            on:
                workflow_call:
                    inputs:
                        input1:
                            type: string
                        input2:
                            type: string
                        input3:
                            type: boolean
                            default: ${{ inputs.input3 }}
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["property \"input3\" is not defined in \"inputs\" context"]),
            // input2 references input1 (already defined) → OK
            new RuleCase(
            "ok-input-default-back-ref",
            """
            on:
                workflow_call:
                    inputs:
                        input1:
                            type: string
                        input2:
                            type: string
                            default: ${{ inputs.input1 }}
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_FromJsonBrokenJson()
    {
        // fromJSON validation is done in the parser (not linter rule), so diagnostics have RuleId=null
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                foo:
                    strategy:
                        matrix:
                            include:
                                - invalid1: ${{ fromJSON('"foo') }}
                                - invalid2: ${{ fromJSON('["foo"') }}
                                - invalid3: ${{ fromJSON('') }}
                                - valid: ${{ fromJSON('"hello"') }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """);
        using var result = new LintEngine([]).Check(Encoding.UTF8.GetBytes(yaml), "fromjson-test.yml");
        var fromJsonErrors = result.Diagnostics
            .Where(x => x.Message.Contains("fromJSON()", StringComparison.Ordinal) && x.Message.Contains("JSON", StringComparison.Ordinal))
            .ToArray();

        // 3 broken JSON errors, none for valid JSON
        await Assert.That(fromJsonErrors).Count().IsEqualTo(3);
        await Assert.That(fromJsonErrors[0].Message).Contains("not valid JSON");
        await Assert.That(fromJsonErrors[1].Message).Contains("not valid JSON");
        await Assert.That(fromJsonErrors[2].Message).Contains("not valid JSON");
    }

    [Test]
    public async Task RuleRegression_ExpressionParser_DoubleQuoteDetection()
    {
        // Double-quote in expression should produce a parse error suggesting single quotes
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                foo:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          continue-on-error: ${{ env.OS == "macos-latest" }}
            """);
        using var result = new LintEngine([]).Check(Encoding.UTF8.GetBytes(yaml), "issue193.yml");

        // Parser diagnostics have RuleId=null. Check all diagnostics.
        var hasDoubleQuoteError = result.Diagnostics.Any(x =>
            x.Message.Contains("'\"'", StringComparison.Ordinal) &&
            x.Message.Contains("single quote", StringComparison.OrdinalIgnoreCase));
        await Assert.That(hasDoubleQuoteError).IsTrue();
    }

    [Test]
    public async Task RuleRegression_RunEnvContextDirectUseRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-run-uses-shell-variable-only",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        VERSION: 1.2.3
                    steps:
                        - run: echo "$VERSION"
            """,
            []),
            new RuleCase(
            "ok-run-uses-non-env-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref_name }}"
            """,
            []),
            new RuleCase(
            "ng-run-uses-env-dot-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        VERSION: 1.2.3
                    steps:
                        - run: echo "${{ env.VERSION }}"
            """,
            ["must not reference", "env.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-env-bracket-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        VERSION: 1.2.3
                    steps:
                        - run: echo "${{ env['VERSION'] }}"
            """,
            ["must not reference", "env.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-env-in-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        VERSION: 1.2.3
                    steps:
                        - run: echo "${{ format('{0}', env.VERSION) }}"
            """,
            ["must not reference", "env.*", "shell variables"]),
        };

        await AssertRuleCases(new RunEnvContextDirectUseRule(), "run-env-context-direct-use", cases);
    }

    [Test]
    public async Task RuleRegression_RunSecretsContextDirectUseRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-run-uses-shell-variable-only",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        TOKEN: ${{ secrets.MY_TOKEN }}
                    steps:
                        - run: echo "$TOKEN"
            """,
            []),
            new RuleCase(
            "ok-run-uses-non-secrets-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref_name }}"
            """,
            []),
            new RuleCase(
            "ng-run-uses-secrets-dot-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ secrets.MY_TOKEN }}"
            """,
            ["must not reference", "secrets.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-secrets-bracket-access",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ secrets['MY_TOKEN'] }}"
            """,
            ["must not reference", "secrets.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-secrets-in-function",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ format('{0}', secrets.MY_TOKEN) }}"
            """,
            ["must not reference", "secrets.*", "shell variables"]),
        };

        await AssertRuleCases(new RunSecretsContextDirectUseRule(), "run-secrets-context-direct-use", cases);
    }

    [Test]
    public async Task RuleRegression_RunInputsContextDirectUseRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-run-uses-shell-variable-only",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        TARGET: ${{ inputs.target }}
                    steps:
                        - run: echo "$TARGET"
            """,
            []),
            new RuleCase(
            "ok-block-run-does-not-bleed-into-env-or-next-step-if",
            """
            name: ci
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                    - name: benchmark
                        run: |
                            dotnet run --filter "${FILTER}"
                            echo "result=success" >> "$GITHUB_OUTPUT"
                        env:
                            FILTER: ${{ inputs.target }}
                    - name: report
                        run: |
                            echo first

                            echo second
                    - name: update
                        if: ${{ inputs.target == '*' }}
                        run: |
                            echo done
            """.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            []),
            new RuleCase(
            "ok-run-uses-non-inputs-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref_name }}"
            """,
            []),
            new RuleCase(
            "ng-run-uses-inputs-dot-access",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ inputs.target }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-inputs-bracket-access",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ inputs['target'] }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-github-event-inputs-dot-access",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.inputs.target }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-inputs-in-function",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ format('{0}', inputs.target) }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
        };

        await AssertRuleCases(new RunInputsContextDirectUseRule(), "run-inputs-context-direct-use", cases);
    }

    [Test]
    public async Task RuleRegression_SecretsWholeContextAccessRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-specific-key-in-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        MY_SECRET: ${{ secrets.MY_TOKEN }}
                    steps:
                        - run: echo "$MY_SECRET"
            """,
            []),
            new RuleCase(
            "ok-no-secrets-reference",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref_name }}"
            """,
            []),
            new RuleCase(
            "ng-run-tojson-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ toJson(secrets) }}"
            """,
            ["must not reference", "secrets", "context object"]),
            new RuleCase(
            "ng-step-env-tojson-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/some-action@v1
                          env:
                            ALL_SECRETS: ${{ toJson(secrets) }}
            """,
            ["must not reference", "secrets", "context object"]),
            new RuleCase(
            "ng-step-with-tojson-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/some-action@v1
                          with:
                            all-secrets: ${{ toJson(secrets) }}
            """,
            ["must not reference", "secrets", "context object"]),
            new RuleCase(
            "ng-job-env-tojson-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        ALL_SECRETS: ${{ toJson(secrets) }}
                    steps:
                        - run: echo ok
            """,
            ["must not reference", "secrets", "context object"]),
            new RuleCase(
            "ng-format-function-whole-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ format('{0}', secrets) }}"
            """,
            ["must not reference", "secrets", "context object"]),
        };

        await AssertRuleCases(new SecretsWholeContextAccessRule(), "secrets-whole-context-access", cases);
    }

    [Test]
    public async Task RuleRegression_ConcurrencyLimitsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-workflow-concurrency-with-cancel-true",
            """
            on: push
            concurrency:
                group: ${{ github.workflow }}-${{ github.ref }}
                cancel-in-progress: true
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-workflow-concurrency-with-cancel-false",
            """
            on: push
            concurrency:
                group: ${{ github.workflow }}-${{ github.ref }}
                cancel-in-progress: false
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-workflow-concurrency-with-cancel-expression",
            """
            on: push
            concurrency:
                group: ${{ github.workflow }}-${{ github.ref }}
                cancel-in-progress: ${{ github.event_name == 'pull_request' }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-job-concurrency-with-cancel-true",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    concurrency:
                        group: ${{ github.workflow }}-${{ github.ref }}
                        cancel-in-progress: true
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-job-concurrency-with-cancel-false",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    concurrency:
                        group: ${{ github.workflow }}-${{ github.ref }}
                        cancel-in-progress: false
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-reusable-only-workflow",
            """
            on: workflow_call
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-reusable-workflow-call-job",
            """
            on: push
            concurrency:
                group: ${{ github.workflow }}-${{ github.ref }}
                cancel-in-progress: true
            jobs:
                reuse:
                    uses: owner/repo/.github/workflows/reuse.yml@main
            """,
            []),
            new RuleCase(
            "ok-workflow-concurrency-covers-all-jobs",
            """
            on: push
            concurrency:
                group: ${{ github.workflow }}-${{ github.ref }}
                cancel-in-progress: true
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-workflow-call-mixed-triggers",
            """
            on:
                push:
                workflow_call:
            concurrency:
                group: ${{ github.workflow }}-${{ github.ref }}
                cancel-in-progress: true
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-concurrency-bare",
            """
            on: push
            concurrency: my-group
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["missing 'cancel-in-progress'"]),
            new RuleCase(
            "ng-no-concurrency-anywhere",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["does not declare concurrency"]),
            new RuleCase(
            "ng-job-concurrency-bare",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    concurrency: my-group
                    steps:
                        - run: echo ng
            """,
            ["missing 'cancel-in-progress'"]),
            new RuleCase(
            "ng-mixed-jobs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    concurrency: my-group
                    steps:
                        - run: echo ng
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["missing 'cancel-in-progress'", "does not declare concurrency"]),
        };

        // concurrency-limits is opt-in; provide config that enables it.
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["concurrency-limits"] = new RuleConfig { Enabled = true },
            },
        };

        await AssertRuleCases(new ConcurrencyLimitsRule(), "concurrency-limits", cases, config);
    }

    [Test]
    public async Task RuleRegression_ConcurrencyLimitsRule_DisabledByDefault()
    {
        // concurrency-limits is opt-in: LintEngine.Check without config must NOT emit its diagnostics.
        var yaml = System.Text.Encoding.UTF8.GetBytes(NormalizeYaml("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hello
            """));
        var engine = new LintEngine();
        using var result = engine.Check(yaml, ".github/workflows/test.yml");
        await Assert.That(result.Diagnostics.Where(d => d.RuleId == "concurrency-limits").ToArray()).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ConcurrencyLimitsRule_EnabledWithConfig()
    {
        // concurrency-limits emits diagnostics when explicitly enabled via config.
        var yaml = System.Text.Encoding.UTF8.GetBytes(NormalizeYaml("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo hello
            """));
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["concurrency-limits"] = new RuleConfig { Enabled = true },
            },
        };
        var engine = new LintEngine();
        using var result = engine.Check(yaml, ".github/workflows/test.yml", config);
        await Assert.That(result.Diagnostics.Where(d => d.RuleId == "concurrency-limits").ToArray()).IsNotEmpty();
    }

    [Test]
    public async Task RuleRegression_UnsoundConditionRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-plain-fenced-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: ${{ github.event_name == 'push' }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-strip-chomping-literal",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: |-\n      ${{ github.event_name == 'push' }}\n    steps:\n      - run: echo ok\n",
            []),
            new RuleCase(
            "ok-strip-chomping-folded",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: >-\n      ${{ github.event_name == 'push' }}\n    steps:\n      - run: echo ok\n",
            []),
            new RuleCase(
            "ok-no-expression",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: |\n      true\n    steps:\n      - run: echo ok\n",
            []),
            new RuleCase(
            "ng-literal-block-scalar-with-fenced-expr",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: |\n      ${{ github.event_name == 'push' }}\n    steps:\n      - run: echo ng\n",
            ["always truthy", "strip chomping"]),
            new RuleCase(
            "ng-folded-block-scalar-with-fenced-expr",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    if: >\n      ${{ github.event_name == 'push' }}\n    steps:\n      - run: echo ng\n",
            ["always truthy", "strip chomping"]),
            new RuleCase(
            "ng-step-block-scalar-with-fenced-expr",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - if: |\n          ${{ github.event_name == 'push' }}\n        run: echo ng\n",
            ["always truthy", "strip chomping"]),
        };

        await AssertRuleCases(new UnsoundConditionRule(), "unsound-condition", cases);
    }

    [Test]
    public async Task RuleRegression_UnpinnedToolsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-unrelated-action",
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
            "ok-pinned-version",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: aquasecurity/setup-trivy@v0.2.0
                          with:
                            version: v0.50.0
            """,
            []),
            new RuleCase(
            "ng-no-version-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: aquasecurity/setup-trivy@v0.2.0
            """,
            ["does not specify 'version'", "unpinned latest"]),
            new RuleCase(
            "ng-version-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: aquasecurity/setup-trivy@v0.2.0
                          with:
                            version: latest
            """,
            ["version: latest", "unpinned"]),
            new RuleCase(
            "ng-version-dynamic-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: aquasecurity/setup-trivy@v0.2.0
                          with:
                            version: ${{ inputs.trivy-version }}
            """,
            ["dynamically", "unpinned"]),
            new RuleCase(
            "ng-case-insensitive-owner-repo",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: AquaSecurity/Setup-Trivy@v0.2.0
            """,
            ["does not specify 'version'", "unpinned latest"]),
        };

        await AssertRuleCases(new UnpinnedToolsRule(), "unpinned-tools", cases);
    }

    [Test]
    public async Task RuleRegression_UnpinnedToolsRule_ActionMetadataCompositeStep_Warns()
    {
        var yaml = NormalizeYaml("""
            name: demo
            description: demo composite action
            runs:
              using: composite
              steps:
                - uses: aquasecurity/setup-trivy@v0.2.0
        """);

        using var result = new LintEngine([new UnpinnedToolsRule()]).Check(
            Encoding.UTF8.GetBytes(yaml),
            ".github/actions/demo/action.yml");

        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "unpinned-tools").ToArray();
        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(diagnostics[0].Message.Contains("does not specify 'version'", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task RuleRegression_UnsoundContainsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "error-contains-github-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: contains('refs/heads/main refs/heads/develop', github.ref)
                    steps:
                        - run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "ok-fromjson-array-contains",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: contains(fromJSON('["main", "develop"]'), github.ref)
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "info-non-controllable-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: contains('push pull_request', github.event_name)
                    steps:
                        - run: echo test
            """,
            ["context reference", "substring bypass"]),
            new RuleCase(
            "error-or-contains-head-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: false || contains('main,develop', github.head_ref)
                    steps:
                        - run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "error-not-contains-base-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: "!contains('main|develop', github.base_ref)"
                    steps:
                        - run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "ok-no-contains",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.ref == 'refs/heads/main'
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "error-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: contains('expected_value', env.MY_VAR)
                          run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "error-inputs-context",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: string
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: contains('main develop', inputs.target)
                    steps:
                        - run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "error-fenced-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: ${{ contains('refs/heads/main', github.ref) }}
                    steps:
                        - run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            // --- Index-style context tests (zizmor parity) ---
            new RuleCase(
            "error-index-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: contains('expected_value', env['MY_VAR'])
                          run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "error-index-github-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: contains('refs/heads/main', github['ref'])
                    steps:
                        - run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "info-index-non-controllable",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: contains('push pull_request', github['event_name'])
                    steps:
                        - run: echo test
            """,
            ["context reference", "substring bypass"]),
        };

        await AssertRuleCases(new UnsoundContainsRule(), "unsound-contains", cases);
    }

    [Test]
    public async Task RuleRegression_BotConditionsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "warning-actor-dependabot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-actor-id-known-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor_id == '49699333'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-actor-id-known-bot-number",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor_id == 49699333
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "ok-actor-id-unknown",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor_id == '123456789'
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "warning-actor-github-actions-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'github-actions[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-triggering-actor-renovate",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.triggering_actor != 'renovate[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-pr-sender-login",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.event.pull_request.sender.login == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "ok-event-name-push",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.event_name == 'push'
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "ok-actor-not-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'my-user'
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "warning-pr-sender-id-known-bot",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.event.pull_request.sender.id == '41898282'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-step-actor-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.actor == 'dependabot[bot]'
                          run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            // --- Index-style context tests (zizmor parity) ---
            new RuleCase(
            "warning-index-actor-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github['actor'] == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-index-actor-case-insensitive",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github['ACTOR'] == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-index-actor-id-known-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github['ACTOR_ID'] == 49699333
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-mixed-index-pr-sender-login",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.event['pull_request'].sender['login'] == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-index-pr-sender-id-known-bot",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github['event']['pull_request']['sender']['id'] == '41898282'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
        };

        await AssertRuleCases(new BotConditionsRule(), "bot-conditions", cases);
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_TableDriven()
    {
        var cases = new[]
        {
            // Case 1: checkout (no persist-credentials) + upload-artifact v4 (path: ., include-hidden-files: true) → error
            new RuleCase(
            "ng-checkout-upload-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Case 2: checkout (persist-credentials: false) + upload-artifact (path: .) → OK
            new RuleCase(
            "ok-checkout-persist-false",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
            """,
            []),
            // Case 3: checkout (no persist-credentials) + upload-artifact (path: dist/) → OK (safe path)
            new RuleCase(
            "ok-safe-path",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: dist/
            """,
            []),
            // Case 4: checkout v6+ (no persist-credentials) + upload-artifact v4 (path: .) is safe.
            // v6+ credentials live under $RUNNER_TEMP, so current-dir upload does not reach them.
            new RuleCase(
            "ok-checkout-v6-upload-dot-hidden",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            []),
            new RuleCase(
            "ok-checkout-uppercase-v6-upload-dot-hidden",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@V6
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
                        []),
            // Edge case: checkout @v6-legacy should be treated as non-v6+ (arbitrary ref, error not warning)
            new RuleCase(
            "ng-checkout-v6-legacy-upload-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6-legacy
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: checkout @v6.1 is valid semver v6+, and current-dir upload remains safe.
            new RuleCase(
            "ok-checkout-v6-1-upload-dot-hidden",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6.1
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
                        []),
            // Case 5: checkout only (no upload-artifact) → OK
            new RuleCase(
            "ok-checkout-only",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
            // Case 6: upload-artifact only (no checkout) → OK
            new RuleCase(
            "ok-upload-only",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
            """,
            []),
            // Edge case: path: .. (parent directory) + hidden files → error
            new RuleCase(
            "ng-checkout-upload-dotdot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ..
                              include-hidden-files: true
            """,
            ["upload-artifact with path '..'", "persist-credentials: false"]),
            // Edge case: path: ${{ github.workspace }} + hidden files → error
            new RuleCase(
            "ng-checkout-upload-workspace",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ${{ github.workspace }}
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: persist-credentials expression is treated conservatively as unsafe
            new RuleCase(
            "ng-persist-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: ${{ inputs.persist_credentials }}
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: include-hidden-files expression is treated conservatively as potentially true
            new RuleCase(
            "ng-include-hidden-files-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: ${{ inputs.include_hidden }}
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: persist-credentials: true → still flagged
            new RuleCase(
            "ng-persist-true",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: SHA-pinned checkout → treated as non-v6+ (unknown version)
            new RuleCase(
            "ng-checkout-sha-upload-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            new RuleCase(
            "ng-checkout-upload-root-equivalent-dot-slash-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ./.
                              include-hidden-files: true
            """,
            ["upload-artifact with path './.'", "persist-credentials: false"]),
            new RuleCase(
            "ng-checkout-upload-root-equivalent-dot-double-slash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .//
                              include-hidden-files: true
            """,
            ["upload-artifact with path './/'", "persist-credentials: false"]),
            new RuleCase(
            "ng-checkout-upload-parent-equivalent-dotdot-slash-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ../.
                              include-hidden-files: true
            """,
            ["upload-artifact with path '../.'", "persist-credentials: false"]),
            new RuleCase(
            "ng-checkout-upload-workspace-suffix",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ${{ github.workspace }}/.
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: upload-artifact before checkout should not be reported because checkout runs later.
            new RuleCase(
            "ok-upload-before-checkout",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
                        - uses: actions/checkout@v4
            """,
            []),
            // Edge case: upload-artifact v4 with arbitrary branch/tag ref like @v4-legacy should be treated conservatively
            new RuleCase(
            "ng-checkout-upload-v4-legacy-tag",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4-legacy
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: @v4. (dot but no minor digits) should be treated conservatively
            new RuleCase(
            "ng-checkout-upload-v4-dot-only",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: @v4.x (non-numeric minor) should be treated conservatively
            new RuleCase(
            "ng-checkout-upload-v4-dot-x",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.x
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: @v4.4-legacy (suffix after minor) should be treated conservatively
            new RuleCase(
            "ng-checkout-upload-v4-4-legacy",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.4-legacy
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: @v4.6.2 (patch version) should be accepted as v4.6 (safe, no hidden files by default)
            new RuleCase(
            "ok-checkout-upload-v4-6-2-no-hidden",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.6.2
                          with:
                              name: my-artifact
                              path: .
            """,
            []),
            // Edge case: @v4.3.1 (patch version, minor < 4) should be treated as unsafe (hidden files by default)
            new RuleCase(
            "ng-checkout-upload-v4-3-1-hidden-default",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.3.1
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: @v4.6.2-legacy (patch with suffix) should be treated conservatively
            new RuleCase(
            "ng-checkout-upload-v4-6-2-legacy",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.6.2-legacy
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: backslash path separators (Windows-style) should be treated as dangerous
            new RuleCase(
            "ng-checkout-upload-backslash-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .\
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            new RuleCase(
            "ng-checkout-upload-backslash-dotdot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ..\
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: github.workspace with backslash trailing
            new RuleCase(
            "ng-checkout-upload-workspace-backslash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ${{ github.workspace }}\
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: path with embedded newlines should be escaped in diagnostics
            new RuleCase(
            "ng-checkout-upload-multiline-path-escaped",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: |
                                  .
                                  extra
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.\\n", "persist-credentials: false"]),
            // Edge case: ${{ github.workspace }}/.. uploads parent directory (dangerous)
            new RuleCase(
            "ng-checkout-upload-workspace-dotdot-suffix",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ${{ github.workspace }}/..
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: ${{ github.workspace }}\.. (backslash) uploads parent directory (dangerous)
            new RuleCase(
            "ng-checkout-upload-workspace-backslash-dotdot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ${{ github.workspace }}\..
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: ./** glob pattern uploads everything recursively (dangerous)
            new RuleCase(
            "ng-checkout-upload-glob-dot-star-star",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: ./**
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: ** alone matches everything from root (dangerous)
            new RuleCase(
            "ng-checkout-upload-glob-double-star",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: "**"
                              include-hidden-files: true
            """,
            ["upload-artifact with path", "persist-credentials: false"]),
            // Edge case: checkout v6+ still leaks credentials when parent-directory upload can include $RUNNER_TEMP.
            new RuleCase(
            "ng-checkout-v6-upload-parent-dir-without-hidden-files",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: my-artifact
                              path: ../..
            """,
            ["upload-artifact with path '../..'", "persist-credentials: false"]),
            // Negative case: v6+ checkout + current-dir upload + no hidden files is safe.
            // v6+ credentials are in $RUNNER_TEMP (not .git/config), and hidden files excluded,
            // so current-dir upload does not expose credentials.
            new RuleCase(
            "ok-checkout-v6-upload-dot-no-hidden",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: my-artifact
                              path: .
            """,
            []),
            // Edge case: both legacy and v6+ checkout + parent-dir upload + hidden files excluded.
            // Legacy .git/config is protected by hidden-file filter; only v6+ $RUNNER_TEMP concern
            // remains, so severity should be warning (not error).
            new RuleCase(
            "ng-checkout-both-parent-dir-no-hidden-warning",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: my-artifact
                              path: ../..
            """,
            ["upload-artifact with path '../..'", "$RUNNER_TEMP"]),
            // Edge case: SHA-pinned checkout has unknown version — conservatively assumes both risks.
            // With parent-dir upload and hidden files excluded, $RUNNER_TEMP risk yields warning.
            new RuleCase(
            "ng-checkout-sha-parent-dir-no-hidden-warning",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: my-artifact
                              path: ../..
            """,
            ["upload-artifact with path '../..'", "$RUNNER_TEMP"]),
            // Edge case: leading-zero checkout refs are arbitrary tags, not semver v6+.
            new RuleCase(
            "ng-checkout-v06-upload-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v06
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: .
                              include-hidden-files: true
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Edge case: leading-zero upload refs are arbitrary tags, so hidden-file defaults stay unknown and conservative.
            new RuleCase(
            "ng-checkout-v4-upload-v04-dot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v04
                          with:
                              name: my-artifact
                              path: .
            """,
            ["upload-artifact with path '.'", "persist-credentials: false"]),
            // Safe case: dist/** is NOT dangerous (subdirectory glob)
            new RuleCase(
            "ok-checkout-upload-glob-subdir",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: my-artifact
                              path: dist/**
                              include-hidden-files: true
            """,
            []),
        };

        await AssertRuleCases(new ArtipackedRule(), "artipacked", cases);
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ReportsAllDangerousUploadsInLargeJob()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-01
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-02
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-03
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-04
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-05
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-06
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-07
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-08
                              path: .
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-09
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-many-uploads.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(9);
        await Assert.That(diagnostics.All(x => x.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_DoesNotMissUnsafeCheckoutAfterSafeOnes()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: false
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-late-checkout.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ReportsOnlyUploadsAfterUnsafeCheckout()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/upload-artifact@v4
                          with:
                              name: before-checkout
                              path: .
                              include-hidden-files: true
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: after-checkout
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-ordered-uploads.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).Contains("upload-artifact with path '.'");
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(15);
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_V6PlusCurrentDirWithHiddenFilesIsSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_DoesNotReportUploadArtifactV4_WhenHiddenFilesAreDefaultedOff()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v4-default-hidden-files.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ReportsUploadArtifactV4_WhenHiddenFilesAreIncluded()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v4-include-hidden.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_DoesNotReportUploadArtifactV4_WhenHiddenFilesAreExplicitlyDisabled()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: false
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v4-hidden-disabled.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ReportsPathValueLocation()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-location.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");
        var lines = yaml.Split('\n');
        var pathLineIndex = Array.FindIndex(lines, static x => x.Contains("path: .", StringComparison.Ordinal));
        var pathLine = lines[pathLineIndex];
        var expectedStartColumn = pathLine.IndexOf('.', StringComparison.Ordinal) + 1;
        var expectedLine = pathLineIndex + 1;

        await Assert.That(diagnostic.Location.StartLine).IsEqualTo(expectedLine);
        await Assert.That(diagnostic.Location.StartColumn).IsEqualTo(expectedStartColumn);
        await Assert.That(diagnostic.Location.EndLine).IsEqualTo(expectedLine);
        await Assert.That(diagnostic.Location.EndColumn).IsEqualTo(expectedStartColumn);
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ReportsMultipleParentDirectorySegments()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ../..
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-dotdotdot.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ReportsDeepParentPath()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ../../.
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-deep-parent.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_CaseInsensitivePersistCredentialsFalse()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: False
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-case-insensitive.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_QuotedPersistCredentialsFalse()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              persist-credentials: 'false'
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-quoted-persist-false.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_CaseInsensitiveIncludeHiddenFilesTrue()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: True
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-case-hidden.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_QuotedIncludeHiddenFilesTrue()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: 'true'
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-quoted-hidden-true.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_UploadArtifactV4_3_TreatsAsUnsafe()
    {
        // upload-artifact v4.0-v4.3 included hidden files by default
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.3
                          with:
                              name: artifact
                              path: .
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v4.3.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_UploadArtifactV4_4_IsSafeByDefault()
    {
        // upload-artifact v4.4+ excludes hidden files by default
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: .
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v4.4.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_UploadArtifactV5_IsConservativeByDefault()
    {
        // Only v4 behavior is modeled precisely. Newer major versions are treated
        // conservatively unless hidden file behavior is explicitly known.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v5
                          with:
                              name: artifact
                              path: .
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v5.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_UploadArtifactV5_ExplicitlyDisablingHiddenFilesSuppressesLegacyCase()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v5
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: false
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v5-hidden-disabled.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_UnknownUploadArtifactRef_RemainsConservativeEvenWhenHiddenFilesDisabled()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@main
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: false
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-main-hidden-disabled.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_BothCheckoutsParentDirNoHiddenIsWarning()
    {
        // When both legacy and v6+ checkout are present but hidden files excluded,
        // legacy .git/config is protected by hidden-file filter. Only v6+ $RUNNER_TEMP
        // is at risk via parent-dir, so severity should be warning (not error).
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../..
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-both-parent.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_BothCheckoutsWithHiddenFilesIsError()
    {
        // When both legacy and v6+ checkout are present AND hidden files included,
        // legacy .git/config IS exposed, so severity should be error.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-both-hidden.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains(".git/config");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ShaPinnedCheckoutParentDirNoHiddenIsWarning()
    {
        // SHA-pinned checkout has unknown version — could be v6+.
        // With parent-dir upload and hidden files excluded, $RUNNER_TEMP may be at risk → WARNING.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../..
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-sha-parent.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ShaPinnedCheckoutWithHiddenFilesIsError()
    {
        // SHA-pinned checkout has unknown version — could be legacy.
        // With hidden files included, .git/config is at risk → ERROR.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: .
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-sha-hidden.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains(".git/config");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ShaPinnedCheckoutCurrentDirNoHiddenIsSafe()
    {
        // SHA-pinned checkout with current-dir upload and hidden files excluded.
        // Legacy .git/config is hidden (safe), v6+ $RUNNER_TEMP is not in current dir (safe) → no diagnostic.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: .
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-sha-safe.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_MultilinePathAccumulatesParentDir()
    {
        // Multi-line path: first line is "." (current-dir, not parent-exposing),
        // second line is "../.." (parent-dir). The rule must scan all lines to
        // accumulate exposesParentDirectory correctly.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: |
                                  .
                                  ../..
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-multiline.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_MultilinePathExcludingGitDirectoryIsSafe()
    {
        // Multi-line artifact paths support exclusion globs. When the root is uploaded
        // but .git is excluded, legacy checkout credentials in .git/config are not exposed.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-multiline-exclude-git.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_MultilinePathExcludingGitConfigIsSafe()
    {
        // Excluding .git/config directly should also suppress the legacy checkout
        // credential exposure case.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !.git/config
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-multiline-exclude-git-config.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_BareGitExclusionDoesNotSuppressWarning()
    {
        // !.git (bare) does NOT exclude .git/config in @actions/glob — only !.git/** or !.git/config does
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !.git
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-bare-git-exclusion.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_GitDirectoryExclusionDoesNotSuppressNestedCheckoutPath()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-exclude-root-git-nested-checkout.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_GitConfigExclusionDoesNotSuppressNestedCheckoutPath()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !.git/config
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-exclude-root-git-config-nested-checkout.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_NestedGitDirectoryExclusionIsSafeForNestedCheckoutPath()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !repo/.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-exclude-nested-git-directory.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_InterleavedNestedCheckoutExclusionsApplyPerCheckout()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo-a
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-a
                              path: |
                                  .
                                  !repo-a/.git/**
                              include-hidden-files: true
                        - uses: actions/checkout@v4
                          with:
                              path: repo-b
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-b
                              path: |
                                  .
                                  !repo-a/.git/**
                              include-hidden-files: true
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact-c
                              path: |
                                  .
                                  !repo-a/.git/**
                                  !repo-b/.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-interleaved-nested-exclusions.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
        // Verify the diagnostic targets artifact-b's path (line 22 = "path: |", content starts line 23)
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(23);
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_DeepNestedGitDirectoryExclusionIsSafe()
    {
        var nestedCheckoutPath = string.Join("/", Enumerable.Range(1, 64).Select(index => $"segment-{index:D2}"));
        var yaml = NormalizeYaml(
            $$"""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: {{nestedCheckoutPath}}
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !{{nestedCheckoutPath}}/.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-deep-nested-git-directory-exclusion.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_LegacyGitExclusionDoesNotSuppressV6ParentDirectoryWarning()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: |
                                  ../..
                                  !.git/**
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-parent-with-legacy-exclusion.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_NegativePatternExcludingRunnerTempSuppressesV6Warning()
    {
        // !../../_temp/** after ../.. explicitly excludes $RUNNER_TEMP content,
        // so the v6+ credential exposure warning should be suppressed.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: |
                                  ../..
                                  !../../_temp/**
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-parent-with-temp-exclusion.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_BareNegativePatternWithoutGlobDoesNotSuppressV6Warning()
    {
        // !../../_temp (without trailing glob) does not exclude files UNDER the directory,
        // so the v6+ credential exposure warning must NOT be suppressed.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: |
                                  ../..
                                  !../../_temp
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-parent-with-bare-temp-exclusion.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_WorkspacePrefixedRunnerTempExclusionSuppressesV6Warning()
    {
        // Workspace-prefixed exclusions should behave like other workspace-relative
        // artipacked paths and suppress the v6+ warning when they exclude temp contents.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: |
                                  ../..
                                  !${{ github.workspace }}/../../_temp/**
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-parent-with-workspace-temp-exclusion.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ShallowRunnerTempWildcardDoesNotSuppressV6Warning()
    {
        // !_temp/* only excludes immediate children and does not cover the full
        // runner-temp subtree where checkout v6+ credentials may live.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: |
                                  ../..
                                  !../../_temp/*
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-parent-with-shallow-temp-exclusion.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_NestedCheckoutUploadPathWithoutRootLikeExpansionRemainsDeferred()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: repo
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-nested-upload-path-deferred.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_GitConfigSubpathExclusionIsNotSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !.git/config/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-exclude-git-config-subpath.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_InternalWhitespaceInCheckoutPathDoesNotMatchDifferentExclusionPath()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo /nested
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !repo/nested/.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-checkout-path-with-internal-whitespace.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_BracketWorkspacePathIsFlagged()
    {
        // Bracket-style workspace access is equivalent to github.workspace and should
        // be treated as a dangerous root-like path.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ github['workspace'] }}
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-bracket-workspace.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_DoubleQuotedBracketWorkspacePathIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ github['workspace'] }}
                              include-hidden-files: true
            """).Replace("github['workspace']", "github[\"workspace\"]", StringComparison.Ordinal);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-double-quoted-bracket-workspace.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_UppercaseWorkspacePathIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ GITHUB.workspace }}
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-uppercase-workspace.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_RootFileGlobIsNotFlagged()
    {
        // A narrow root file glob does not recursively sweep the checkout root and
        // should not be treated like ./** or **.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: '*.txt'
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-root-file-glob.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_RootSingleWildcardIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: '*'
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-root-single-wildcard.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_DotSlashSingleWildcardIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: './*'
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-dot-slash-single-wildcard.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_V6SingleParentDirectoryIsSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ..
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-single-parent.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_V6WorkspaceSingleParentDirectoryIsSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ${{ github.workspace }}/..
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-workspace-single-parent.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_V6NamedDirectoryTwoLevelsUpIsNotFlagged()
    {
        // ../../some-dir targets a specific non-_temp directory — does NOT reach $RUNNER_TEMP
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../../some-dir
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-named-dir.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_V6SingleLevelTempIsNotFlagged()
    {
        // ../_temp is only 1 level up — NOT the real $RUNNER_TEMP (which is 2 levels up)
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../_temp
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-v6-single-level-temp.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_RootRecursiveGlobWithFilesIsFlagged()
    {
        var yaml = "on: push\n"
            + "jobs:\n"
            + "  build:\n"
            + "    runs-on: ubuntu-latest\n"
            + "    steps:\n"
            + "      - uses: actions/checkout@v4\n"
            + "      - uses: actions/upload-artifact@v4\n"
            + "        with:\n"
            + "          name: artifact\n"
            + "          path: |\n"
            + "            **/*\n"
            + "          include-hidden-files: true\n";

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-root-recursive-with-files.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_WorkspaceRecursiveGlobIsFlagged()
    {
        // Workspace-root recursive glob is equivalent to ./** and should be treated
        // as a dangerous root-like upload.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ github.workspace }}/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-workspace-recursive-glob.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_CurrentDirectoryRecursiveGlobWithFilesIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ./**/*
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-current-recursive-with-files.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_NormalizedRootPathIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: repo/..
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-normalized-root.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_NormalizedWorkspacePathIsFlagged()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ github.workspace }}/repo/..
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-normalized-workspace.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_NormalizedRootPathExcludingGitDirectoryIsSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  repo/..
                                  !repo/../.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-normalized-root-exclude-git.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_NormalizedWorkspacePathExcludingGitConfigIsSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  ${{ github.workspace }}/repo/..
                                  !${{ github.workspace }}/repo/../.git/config
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-normalized-workspace-exclude-git-config.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_NormalizedWorkspaceGitConfigSubpathExclusionIsNotSafe()
    {
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  ${{ github.workspace }}/repo/..
                                  !${{ github.workspace }}/repo/../.git/config/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-normalized-workspace-exclude-git-config-subpath.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ExpressionPathIsNotFlaggedAsDangerous()
    {
        // Dynamic expression path like ${{ inputs.artifact_path }} should not be
        // treated as a dangerous glob — it resolves at runtime and cannot be
        // classified statically.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ inputs.artifact_path }}
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-expr-path.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_WorkspaceSuffixWithoutSeparatorIsNotFlagged()
    {
        // ${{ github.workspace }}.. (no separator) is string concatenation, NOT a parent path.
        // The rule should NOT treat it as ${{ github.workspace }}/.. .
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: ${{ github.workspace }}..
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-workspace-no-separator.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_WorkspaceExclusionWithoutSeparatorDoesNotSuppress()
    {
        // !${{ github.workspace }}.git/** (no separator) is not a valid exclusion.
        // The rule should still flag the upload.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  ${{ github.workspace }}
                                  !${{ github.workspace }}.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-workspace-exclusion-no-separator.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("persist-credentials: false");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_RecursiveWildcardExcludesSuppressesNestedCheckout()
    {
        // !**/.git/** should suppress the warning for a nested checkout at "repo"
        // because ** matches any prefix including "repo".
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                              path: repo
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !**/.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-recursive-wildcard-nested-checkout.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_RecursiveWildcardExcludesSuppressesRootCheckout()
    {
        // !**/.git/** should also suppress the warning for a root checkout (empty path)
        // because ** can match zero segments.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                        - uses: actions/upload-artifact@v4
                          with:
                              name: artifact
                              path: |
                                  .
                                  !**/.git/**
                              include-hidden-files: true
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-recursive-wildcard-root-checkout.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ParentDirectoryWithChildNameIsWarning()
    {
        // ../../_temp escapes the workspace even though it names a child directory.
        // On GitHub-hosted runners this can reach $RUNNER_TEMP, so v6+ checkout should
        // be warned (parent-directory exposure).
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../../_temp
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-parent-with-child.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_WorkspaceParentDirectoryWithChildNameIsWarning()
    {
        // ${{ github.workspace }}/../../_temp escapes the workspace even though it names
        // a child. Should be flagged as parent-directory exposure.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ${{ github.workspace }}/../../_temp
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-workspace-parent-child.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ParentDirectorySingleFileIsNotFlagged()
    {
        // A narrow parent-directory file path is not equivalent to sweeping a parent
        // directory tree or $RUNNER_TEMP. Keep this deferred rather than warning.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../artifact.txt
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-parent-single-file.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ParentDirectoryTempRecursiveGlobIsWarning()
    {
        // ../../_temp/** sweeps $RUNNER_TEMP recursively — should be flagged.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../../_temp/**
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-parent-temp-recursive-glob.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ParentDirectoryTempStarGlobIsWarning()
    {
        // ../../_temp/* sweeps immediate children of $RUNNER_TEMP — should be flagged.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../../_temp/*
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-parent-temp-star-glob.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_ParentDirectoryTempRecursiveStarGlobIsWarning()
    {
        // ../../_temp/**/* sweeps $RUNNER_TEMP recursively — should be flagged.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../../_temp/**/*
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-parent-temp-recursive-star-glob.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_IntermediateBacktrackToRunnerTempIsDetected()
    {
        // ../../foo/../_temp normalizes to ../../_temp — should reach $RUNNER_TEMP.
        // Regression: the intermediate `foo` segment left escapedNamedSegments stale
        // so that the subsequent `_temp` was counted as the 2nd escaped segment.
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v6
                        - uses: actions/upload-artifact@v4.4
                          with:
                              name: artifact
                              path: ../../foo/../_temp
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-parent-backtrack-temp.yml");
        var diagnostic = result.Diagnostics.Single(x => x.RuleId == "artipacked");

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.Message).Contains("$RUNNER_TEMP");
    }

    [Test]
    public async Task RuleRegression_ArtipackedRule_LeadingRecursiveExclusionSuppressesWithExpressionCheckoutPath()
    {
        // !**/.git/** suppresses legacy credential exposure even when checkout
        // path contains an expression (cannot be statically normalized).
        var yaml = NormalizeYaml(
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v3
                          with:
                              path: ${{ matrix.repo_path }}
                        - uses: actions/upload-artifact@v4.3
                          with:
                              name: artifact
                              path: |
                                  .
                                  !**/.git/**
            """);

        using var result = new LintEngine([new ArtipackedRule()]).Check(Encoding.UTF8.GetBytes(yaml), "artipacked-recursive-excl-expr-path.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "artipacked").ToArray();

        await Assert.That(diagnostics).IsEmpty();
    }

    private static async Task AssertRuleCases(IRule rule, string ruleId, RuleCase[] cases, LintConfig? config = null)
    {
        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var yaml = NormalizeYaml(c.Yaml);
            using var result = config is null
                ? new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), $"rule-case-{c.Name}.yml")
                : new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), $"rule-case-{c.Name}.yml", config);
            var diagnostics = result.Diagnostics.Where(x => x.RuleId == ruleId).ToArray();

            if (c.ExpectedSubstrings.Length == 0)
            {
                await Assert.That(diagnostics).IsEmpty();
                continue;
            }

            for (var j = 0; j < c.ExpectedSubstrings.Length; j++)
            {
                var expected = c.ExpectedSubstrings[j];
                var found = diagnostics.Any(x => x.Message.Contains(expected, StringComparison.Ordinal));
                if (!found)
                {
                    var observed = diagnostics.Length == 0
                        ? "<no rule diagnostics>"
                        : string.Join(" | ", diagnostics.Select(static x => x.Message));
                    throw new InvalidOperationException($"rule={ruleId} case={c.Name} expected={expected} observed={observed}");
                }
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "seiton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string NormalizeYaml(string raw)
    {
        var normalized = raw.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');

        var start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        var end = lines.Length - 1;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        if (end < start)
        {
            return string.Empty;
        }

        var minIndent = int.MaxValue;
        for (var i = start; i <= end; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            if (indent < minIndent)
            {
                minIndent = indent;
            }
        }

        if (minIndent == int.MaxValue)
        {
            minIndent = 0;
        }

        var builder = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            var line = lines[i];
            if (line.Length >= minIndent)
            {
                builder.Append(line[minIndent..]);
            }
            else
            {
                builder.Append(line);
            }

            if (i < end)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private readonly record struct RuleCase(string Name, string Yaml, string[] ExpectedSubstrings);

    private readonly record struct FixabilityCase(string RuleId, IRule Rule, string Yaml, bool ExpectsFix);

    private sealed class DuplicateDiagnosticRule : IRule
    {
        private readonly List<Diagnostic> diagnostics = [];

        public DuplicateDiagnosticRule(RuleId id)
        {
            Id = id;
        }

        public RuleId Id { get; }

        public string Name => $"Duplicate-{Id.ToId()}";

        public bool SupportsDocumentKind(DocumentKind documentKind) => true;

        public IReadOnlyList<Diagnostic> GetDiagnostics() => diagnostics;

        public void SetConfig(LintConfig config)
        {
        }

        public void VisitWorkflowPre(Workflow workflow)
        {
            diagnostics.Clear();
            diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "shared duplicate diagnostic",
                    new TextRange(0, 0, 1, 1, 1, 1),
                    RuleId: Id.ToId()));
        }

        public void VisitWorkflowPost(Workflow workflow)
        {
        }

        public void VisitEvent(Event ev)
        {
        }

        public void VisitJobPre(Job job)
        {
        }

        public void VisitJobPost(Job job)
        {
        }

        public void VisitStep(Step step)
        {
        }
    }

    private sealed class ConfigCaptureRule : IRule
    {
        public RuleId Id => RuleId.JobStructure;

        public string Name => "Config Capture Rule";

        public bool SupportsDocumentKind(DocumentKind documentKind) => true;

        public LintConfig? LastConfig { get; private set; }

        public IReadOnlyList<Diagnostic> GetDiagnostics() => [];

        public void SetConfig(LintConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            LastConfig = config;
        }

        public void VisitWorkflowPre(Workflow workflow)
        {
        }

        public void VisitWorkflowPost(Workflow workflow)
        {
        }

        public void VisitEvent(Event ev)
        {
        }

        public void VisitJobPre(Job job)
        {
        }

        public void VisitJobPost(Job job)
        {
        }

        public void VisitStep(Step step)
        {
        }
    }

    private sealed class CountingRule : IRule
    {
        private LintConfig? config;

        public RuleId Id => RuleId.JobStructure;

        public string Name => "Test Rule";

        public bool SupportsDocumentKind(DocumentKind documentKind) => true;

        public int WorkflowPreCount { get; private set; }

        public int WorkflowPostCount { get; private set; }

        public int EventCount { get; private set; }

        public int JobPreCount { get; private set; }

        public int JobPostCount { get; private set; }

        public int StepCount { get; private set; }

        public IReadOnlyList<Diagnostic> GetDiagnostics() => [];

        public void SetConfig(LintConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            this.config = config;
        }

        public void VisitWorkflowPre(Workflow workflow)
        {
            EnsureConfigured();
            WorkflowPreCount++;
        }

        public void VisitWorkflowPost(Workflow workflow)
        {
            EnsureConfigured();
            WorkflowPostCount++;
        }

        public void VisitEvent(Event ev)
        {
            EnsureConfigured();
            EventCount++;
        }

        public void VisitJobPre(Job job)
        {
            EnsureConfigured();
            JobPreCount++;
        }

        public void VisitJobPost(Job job)
        {
            EnsureConfigured();
            JobPostCount++;
        }

        public void VisitStep(Step step)
        {
            EnsureConfigured();
            StepCount++;
        }

        private void EnsureConfigured()
        {
            if (config is null)
            {
                throw new InvalidOperationException("Rule is not configured.");
            }
        }
    }
}
