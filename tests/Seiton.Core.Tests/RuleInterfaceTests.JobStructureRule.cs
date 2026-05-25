using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

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
    public async Task JobStructureRule_CannotHaveBothUsesAndSteps_ReportsAtStepsKeyPosition()
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
        var bothDiag = result.Diagnostics
            .Where(d => d.Message.Contains("cannot have both uses and steps", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(bothDiag).Count().IsEqualTo(1);
        // Must report at the 'steps:' key position (line 5), not the job ID position (line 3)
        await Assert.That(bothDiag[0].Location.StartLine).IsEqualTo(5);
        await Assert.That(bothDiag[0].Location.StartColumn).IsEqualTo(5);
    }


    [Test]
    public async Task JobStructureRule_CannotHaveBothUsesAndRunsOn_ReportsAtRunsOnKeyPosition()
    {
        var yaml = """
        on: push
        jobs:
          call1:
            uses: org/repo/workflow.yml@v1
            runs-on: ubuntu-latest
        """u8;

        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var bothDiag = result.Diagnostics
            .Where(d => d.Message.Contains("cannot have both uses and runs-on", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(bothDiag).Count().IsEqualTo(1);
        // Must report at the 'runs-on:' key position (line 5), not the job ID position (line 3)
        await Assert.That(bothDiag[0].Location.StartLine).IsEqualTo(5);
        await Assert.That(bothDiag[0].Location.StartColumn).IsEqualTo(5);
    }
}
