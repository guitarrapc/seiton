using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

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
            new RuleCase(
            "ok-self-repository-format",
            """
            on: push
            jobs:
                reuse:
                    uses: $/.github/workflows/reuse.yml
            """,
            []),
            new RuleCase(
            "ng-self-repository-with-ref",
            """
            on: push
            jobs:
                reuse:
                    uses: $/.github/workflows/reuse.yml@main
            """,
            ["is not following the format"]),
        };

        await AssertRuleCases(new ReusableWorkflowRule(), "reusable-workflow", cases);
    }















































































    // regression: parser + lint rule duplicate diagnostics are suppressed


    // C-3: hashFiles function context restriction (linter diagnostic)





    // C-4: job-level secrets exclusion



    [Test]
    public async Task ReusableWorkflowRule_InvalidFormat_IncludesDocUrl()
    {
        var yaml = """
        on: push
        jobs:
            reuse:
                uses: "foo/bar/workflow.yml"
        """u8;

        using var result = new LintEngine([new ReusableWorkflowRule()]).Check(yaml.ToArray(), "test.yaml");
        var msgs = result.Diagnostics.Where(d => d.Message.Contains("is not following the format", StringComparison.Ordinal)).ToArray();
        await Assert.That(msgs.Length).IsGreaterThan(0);
        await Assert.That(msgs[0].Message.Contains("see https://docs.github.com/en/actions/learn-github-actions/reusing-workflows for more details", StringComparison.Ordinal)).IsTrue();
    }



    // regression: alias-expanded steps that produce the same error at the same position
    // must be deduplicated even though each step gets a unique step-index prefix.

    // regression: action metadata composite steps with alias expansion must also dedup.
    // steps[N] prefix (no jobs.'<id>') must be stripped for dedup consistency.

    // reusable-workflow forbidden-key diagnostics must report at the forbidden key position
    [Test]
    public async Task ReusableWorkflowRule_ForbiddenKey_ReportsAtKeyPosition()
    {
        var yaml = """
        on: push
        jobs:
          call1:
            uses: org/repo/workflow.yml@v1
            steps:
              - run: echo
        """u8;

        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var forbiddenKeyDiag = result.Diagnostics
            .Where(d => d.Message.Contains("key 'steps' is not allowed", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(forbiddenKeyDiag).Count().IsEqualTo(1);
        // Must report at the 'steps:' key position (line 5), not the job ID position (line 3)
        await Assert.That(forbiddenKeyDiag[0].Location.StartLine).IsEqualTo(5);
        await Assert.That(forbiddenKeyDiag[0].Location.StartColumn).IsEqualTo(5);
    }


    [Test]
    public async Task ReusableWorkflowRule_ForbiddenRunsOn_ReportsAtKeyPosition()
    {
        var yaml = """
        on: push
        jobs:
          call1:
            uses: org/repo/workflow.yml@v1
            runs-on: ubuntu-latest
        """u8;

        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var forbiddenKeyDiag = result.Diagnostics
            .Where(d => d.Message.Contains("key 'runs-on' is not allowed", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(forbiddenKeyDiag).Count().IsEqualTo(1);
        // Must report at the 'runs-on:' key position (line 5), not the job ID position (line 3)
        await Assert.That(forbiddenKeyDiag[0].Location.StartLine).IsEqualTo(5);
        await Assert.That(forbiddenKeyDiag[0].Location.StartColumn).IsEqualTo(5);
    }


    [Test]
    public async Task ReusableWorkflowRule_WithRequiresUses_ReportsAtWithKeyPosition()
    {
        var yaml = """
        on: push
        jobs:
          call2:
            with:
              foo: bar
            runs-on: ubuntu-latest
            steps:
              - run: echo
        """u8;

        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var requiresUsesDiag = result.Diagnostics
            .Where(d => d.Message.Contains("key 'with' requires uses", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(requiresUsesDiag).Count().IsEqualTo(1);
        // Must report at the 'with:' key position (line 4), not the job ID position (line 3)
        await Assert.That(requiresUsesDiag[0].Location.StartLine).IsEqualTo(4);
        await Assert.That(requiresUsesDiag[0].Location.StartColumn).IsEqualTo(5);
    }


    [Test]
    public async Task ReusableWorkflowRule_SecretsRequiresUses_ReportsAtSecretsKeyPosition()
    {
        var yaml = """
        on: push
        jobs:
          call3:
            secrets:
              aaa: bbb
            runs-on: ubuntu-latest
            steps:
              - run: echo
        """u8;

        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var requiresUsesDiag = result.Diagnostics
            .Where(d => d.Message.Contains("key 'secrets' requires uses", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(requiresUsesDiag).Count().IsEqualTo(1);
        // Must report at the 'secrets:' key position (line 4), not the job ID position (line 3)
        await Assert.That(requiresUsesDiag[0].Location.StartLine).IsEqualTo(4);
        await Assert.That(requiresUsesDiag[0].Location.StartColumn).IsEqualTo(5);
    }
}
