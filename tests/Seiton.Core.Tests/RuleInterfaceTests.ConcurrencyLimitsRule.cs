using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

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
}
