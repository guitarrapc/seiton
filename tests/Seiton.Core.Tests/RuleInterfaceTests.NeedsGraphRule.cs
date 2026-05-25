using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

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
}
