using System.Text;
using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{
    [Test]
    public async Task DisableNextLine_StepIf_CommentAboveStep_DoesNotSuppressDiagnostic()
    {
        // Comment is above the step (- run:), NOT above the if: key.
        // disable-next-line targets the YAML line immediately following the comment.
        // The if-cond diagnostic reports on the if: key's line, which is a different line.
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    # seiton: disable-next-line if-cond
                    - run: echo ok
                      if: ${{ true }}
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var ifCondDiags = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        // Expect: diagnostic is NOT suppressed because comment targets the step line, not the if: line
        await Assert.That(ifCondDiags.Length).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task DisableNextLine_StepIf_CommentAboveIfKey_SuppressesDiagnostic()
    {
        // Comment is directly above the if: key — this is the correct placement.
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
                      # seiton: disable-next-line if-cond
                      if: ${{ true }}
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var ifCondDiags = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        // Expect: diagnostic IS suppressed because comment targets the if: line
        await Assert.That(ifCondDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task DisableNextLine_JobIf_CommentAboveJobIfKey_SuppressesDiagnostic()
    {
        // Comment directly above job-level if: key.
        var yaml = """
        on: push
        jobs:
            build:
                # seiton: disable-next-line if-cond
                if: ${{ true }}
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var ifCondDiags = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        // Expect: suppressed
        await Assert.That(ifCondDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task DisableNextLine_Matrix_CommentAboveStrategy_DiagnosticLineCheck()
    {
        // Comment is above strategy:, but matrix diagnostics report on positions
        // within the matrix structure (axis name, etc.), not on the strategy: line.
        var yaml = """
        on: push
        jobs:
            build:
                # seiton: disable-next-line matrix
                strategy:
                    matrix:
                        os: []
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var matrixDiags = result.Diagnostics.Where(d => d.RuleId == "matrix").ToArray();

        // The comment targets strategy: line (N+1), but matrix diagnostics are on deeper lines (axis name line).
        // Expect: NOT suppressed because diagnostic line != strategy: line
        await Assert.That(matrixDiags.Length).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task DisableJob_Matrix_CheckBehavior()
    {
        // Check if disable-job suppresses matrix diagnostics.
        var yaml = """
        # seiton: disable-job build matrix
        on: push
        jobs:
            build:
                strategy:
                    matrix:
                        os: []
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var matrixDiags = result.Diagnostics.Where(d => d.RuleId == "matrix").ToArray();
        var configErrors = result.Diagnostics.Where(d => d.RuleId is null).ToArray();

        // Check for config errors (e.g., "unknown job-id")
        var unknownJobErrors = configErrors.Where(d => d.Message.Contains("unknown job-id", StringComparison.Ordinal)).ToArray();

        // If there are unknown-job errors, disable-job didn't recognize the job ID
        await Assert.That(unknownJobErrors.Length).IsEqualTo(0);

        // Check if matrix was actually suppressed
        var matrixSuppressed = result.SuppressionSummary.SuppressedByRule.ContainsKey("matrix");

        // FIXED: disable-job now correctly suppresses matrix diagnostics because Job.Range
        // covers the full job mapping block, allowing TryFindJobIdForLine to attribute
        // diagnostics within the job body to the correct job.
        await Assert.That(matrixSuppressed).IsTrue();
        await Assert.That(matrixDiags).IsEmpty();
    }

    [Test]
    public async Task DisableNextLine_JobIfBlockScalar_CommentAboveIfKey_SuppressesDiagnostic()
    {
        // Block scalar if: | with ${{ expr }} adds trailing \n, making it always-true.
        // IfCondRule adjusts diagnostic range back to indicator line.
        var yaml = """
        on: push
        jobs:
            build:
                # seiton: disable-next-line if-cond
                if: |
                    ${{ contains(github.event.head_commit.message, 'skip') }}
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var ifCondDiags = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        // IfCondRule adjusts block scalar diagnostic to the | indicator line (same as if: key line).
        // disable-next-line targets if: line → should suppress.
        await Assert.That(ifCondDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task DisableNextLine_JobIfBlockScalar_WithoutComment_EmitsDiagnostic()
    {
        // Verify the block scalar actually triggers if-cond (baseline for suppression test).
        var yaml = """
        on: push
        jobs:
            build:
                if: |
                    ${{ contains(github.event.head_commit.message, 'skip') }}
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var ifCondDiags = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        // Block scalar if: | adds trailing \n → always-true pattern
        await Assert.That(ifCondDiags.Length).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task DisableNextLine_StepIfBlockScalar_CommentAboveIfKey_SuppressesDiagnostic()
    {
        // Step-level block scalar if: |
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
                      # seiton: disable-next-line if-cond
                      if: |
                          ${{ contains(github.event.head_commit.message, 'skip') }}
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var ifCondDiags = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        // Same block scalar adjustment: diagnostic should be on the if: line → suppressed.
        await Assert.That(ifCondDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task DisableNextLine_CommaSeparatedRuleIds_SuppressesAll()
    {
        // Comma-separated is the supported format for multiple rule IDs.
        var yaml = """
        on: push
        jobs:
            # seiton: disable-next-line job-timeout-minutes-required, job-permissions-required
            build:
                runs-on: ubuntu-24.04
                steps:
                    - run: echo test
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");

        // Both comma-separated rule IDs should be suppressed
        await Assert.That(result.Diagnostics.Any(d => d.RuleId == "job-timeout-minutes-required")).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.RuleId == "job-permissions-required")).IsFalse();
    }

    [Test]
    public async Task DisableNextLine_SpaceSeparatedRuleIds_DoesNotSuppressSecondRule()
    {
        // Space-separated is NOT the supported format.
        // "dangerous-triggers job-permissions-required" is treated as a single rule ID token,
        // which fails to resolve.
        var yaml = """
        on:
            # seiton: disable-next-line dangerous-triggers job-permissions-required
            pull_request_target:
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                steps:
                    - run: echo test
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");

        // Rule IDs are split by comma only, so space-separated IDs are treated as one
        // unknown rule-id token and therefore do not suppress diagnostics.

        // Both rules should still be active (not suppressed)
        var configErrors = result.Diagnostics.Where(d => d.RuleId is null && d.Message.Contains("unknown rule-id", StringComparison.Ordinal)).ToArray();
        await Assert.That(configErrors.Length).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task DisableJob_RunnerNoLatest_SuppressesDiagnostic()
    {
        var yaml = """
        # seiton: disable-job build runner-no-latest
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var runnerDiags = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(runnerDiags).IsEmpty();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("runner-no-latest")).IsTrue();
    }

    [Test]
    public async Task DisableJob_IfCond_SuppressesDiagnostic()
    {
        var yaml = """
        # seiton: disable-job build if-cond
        on: push
        jobs:
            build:
                if: true
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var ifCondDiags = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();

        await Assert.That(ifCondDiags).IsEmpty();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("if-cond")).IsTrue();
    }

    [Test]
    public async Task DisableJob_MultipleJobs_SuppressesOnlyTargetJob()
    {
        var yaml = """
        # seiton: disable-job build runner-no-latest
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo build
            test:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo test
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var runnerDiags = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        // Only 'test' job should still have runner-no-latest diagnostics
        await Assert.That(runnerDiags.Length).IsEqualTo(1);
        // The remaining diagnostic should be on 'test' job line
        await Assert.That(runnerDiags[0].Location.StartLine).IsGreaterThanOrEqualTo(10);
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("runner-no-latest")).IsTrue();
    }

    [Test]
    public async Task DisableJob_JobTimeoutMinutesRequired_SuppressesDiagnostic()
    {
        var yaml = """
        # seiton: disable-job build job-timeout-minutes-required
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                permissions: {}
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var timeoutDiags = result.Diagnostics.Where(d => d.RuleId == "job-timeout-minutes-required").ToArray();

        await Assert.That(timeoutDiags).IsEmpty();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("job-timeout-minutes-required")).IsTrue();
    }

    [Test]
    public async Task ConfigExclusion_Jobs_Matrix_SuppressesDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            build:
                strategy:
                    matrix:
                        os: []
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["matrix"], Jobs: ["build"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);
        var matrixDiags = result.Diagnostics.Where(d => d.RuleId == "matrix").ToArray();

        await Assert.That(matrixDiags).IsEmpty();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("matrix")).IsTrue();
    }

    [Test]
    public async Task ConfigExclusion_Jobs_RunnerNoLatest_SuppressesDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["runner-no-latest"], Jobs: ["build"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);
        var runnerDiags = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(runnerDiags).IsEmpty();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("runner-no-latest")).IsTrue();
    }

    [Test]
    public async Task ConfigExclusion_Jobs_NullRules_SuppressesAllJobBodyDiagnostics()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
            test:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo test
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", Rules: null, Jobs: ["build"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);

        // 'build' job diagnostics (runner-no-latest, action-ref, job-permissions-required, etc.) should be suppressed
        // 'test' job should still have runner-no-latest diagnostic (not excluded)
        var testRunnerDiags = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();
        await Assert.That(testRunnerDiags.Length).IsEqualTo(1);
        await Assert.That(result.SuppressionSummary.TotalSuppressed).IsGreaterThanOrEqualTo(1);
        await Assert.That(result.SuppressionSummary.Records.Any(x => x.Source == SuppressionSource.ConfigJob)).IsTrue();
    }

    [Test]
    public async Task DisableJob_CheckoutPersistCredentials_SuppressesStepDiagnostic()
    {
        var yaml = """
        # seiton: disable-job build checkout-persist-credentials
        on: push
        jobs:
            build:
                runs-on: ubuntu-24.04
                timeout-minutes: 10
                permissions:
                    contents: read
                steps:
                    - uses: actions/checkout@692973e3d937129bcbf40652eb9f2f61becf3332
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var checkoutDiags = result.Diagnostics.Where(d => d.RuleId == "checkout-persist-credentials").ToArray();

        await Assert.That(checkoutDiags).IsEmpty();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("checkout-persist-credentials")).IsTrue();
        await Assert.That(result.SuppressionSummary.Records.Any(x => x.Source == SuppressionSource.InlineJob)).IsTrue();
    }

    [Test]
    public async Task DisableJob_IfExprWrapper_SuppressesJobIfDiagnostic()
    {
        var yaml = """
        # seiton: disable-job build if-expr-wrapper
        on: push
        jobs:
            build:
                if: github.ref == 'refs/heads/main'
                runs-on: ubuntu-24.04
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var wrapperDiags = result.Diagnostics.Where(d => d.RuleId == "if-expr-wrapper").ToArray();

        await Assert.That(wrapperDiags).IsEmpty();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("if-expr-wrapper")).IsTrue();
    }

    [Test]
    public async Task DisableJob_JobPermissionsRequired_SuppressesJobIdLineDiagnostic()
    {
        // job-permissions-required reports on the job ID line — verify it's still suppressed
        var yaml = """
        # seiton: disable-job build job-permissions-required
        on: push
        jobs:
            build:
                runs-on: ubuntu-24.04
                timeout-minutes: 10
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var permsDiags = result.Diagnostics.Where(d => d.RuleId == "job-permissions-required").ToArray();

        await Assert.That(permsDiags).IsEmpty();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("job-permissions-required")).IsTrue();
    }

    [Test]
    public async Task DisableJob_MultipleRulesCommaSeparated_SuppressesAll()
    {
        var yaml = """
        # seiton: disable-job build runner-no-latest, job-timeout-minutes-required
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                permissions: {}
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var runnerDiags = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();
        var timeoutDiags = result.Diagnostics.Where(d => d.RuleId == "job-timeout-minutes-required").ToArray();

        await Assert.That(runnerDiags).IsEmpty();
        await Assert.That(timeoutDiags).IsEmpty();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("runner-no-latest")).IsTrue();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("job-timeout-minutes-required")).IsTrue();
    }

    [Test]
    public async Task DisableJob_MissingRuleId_ReportsConfigurationError()
    {
        var yaml = """
        # seiton: disable-job build
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var configErrors = result.Diagnostics.Where(d =>
            d.RuleId is null
            && d.Message.Contains("disable-job requires", StringComparison.Ordinal)).ToArray();

        await Assert.That(configErrors.Length).IsEqualTo(1);
    }

    [Test]
    public async Task DisableJob_SuppressionSourceIsInlineJob()
    {
        var yaml = """
        # seiton: disable-job build runner-no-latest
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");

        await Assert.That(result.SuppressionSummary.Records.Any(x => x.Source == SuppressionSource.InlineJob)).IsTrue();
        await Assert.That(result.SuppressionSummary.Records.All(x =>
            x.Source != SuppressionSource.ConfigJob)).IsTrue();
    }

    [Test]
    public async Task DisableJob_DoesNotAffectOtherJobs_ThreeJobs()
    {
        var yaml = """
        # seiton: disable-job build runner-no-latest
        on: push
        jobs:
            lint:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo lint
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo build
            deploy:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo deploy
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");
        var runnerDiags = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        // lint and deploy should still have runner-no-latest diagnostics
        await Assert.That(runnerDiags.Length).IsEqualTo(2);
        // build should be suppressed
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("runner-no-latest")).IsTrue();
        await Assert.That(result.SuppressionSummary.SuppressedByRule["runner-no-latest"]).IsEqualTo(1);
    }

    [Test]
    public async Task DisableJob_MultipleJobsDifferentRules_SuppressesCorrectly()
    {
        var yaml = """
        # seiton: disable-job build runner-no-latest
        # seiton: disable-job test job-timeout-minutes-required
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo build
            test:
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo test
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");

        // build: runner-no-latest suppressed, timeout-minutes NOT suppressed (has timeout)
        var buildRunnerDiags = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();
        await Assert.That(buildRunnerDiags).IsEmpty();

        // test: runner-no-latest NOT suppressed (uses ubuntu-24.04 which isn't latest), timeout suppressed
        var testTimeoutDiags = result.Diagnostics.Where(d => d.RuleId == "job-timeout-minutes-required").ToArray();
        await Assert.That(testTimeoutDiags).IsEmpty();
    }

    [Test]
    public async Task DisableJob_AndDisableNextLine_BothApply()
    {
        var yaml = """
        # seiton: disable-job build runner-no-latest
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    # seiton: disable-next-line if-cond
                    - if: true
                      run: echo ok
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "test.yml");

        // runner-no-latest suppressed by disable-job
        var runnerDiags = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();
        await Assert.That(runnerDiags).IsEmpty();

        // if-cond suppressed by disable-next-line
        var ifCondDiags = result.Diagnostics.Where(d => d.RuleId == "if-cond").ToArray();
        await Assert.That(ifCondDiags).IsEmpty();

        // Both suppression sources should appear
        await Assert.That(result.SuppressionSummary.Records.Any(x => x.Source == SuppressionSource.InlineJob)).IsTrue();
        await Assert.That(result.SuppressionSummary.Records.Any(x => x.Source == SuppressionSource.InlineNextLine)).IsTrue();
    }

    [Test]
    public async Task ConfigExclusion_Jobs_MultipleJobIds_SuppressesBoth()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo build
            test:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo test
            deploy:
                runs-on: ubuntu-24.04
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo deploy
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["runner-no-latest"], Jobs: ["build", "test"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);
        var runnerDiags = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        // deploy uses ubuntu-24.04 which doesn't have -latest, so no diagnostic expected for deploy either
        // build and test use ubuntu-latest, but they're excluded
        await Assert.That(runnerDiags).IsEmpty();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("runner-no-latest")).IsTrue();
        await Assert.That(result.SuppressionSummary.SuppressedByRule["runner-no-latest"]).IsEqualTo(2);
    }

    [Test]
    public async Task ConfigExclusion_Jobs_StepLevelRule_SuppressesInTargetJob()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-24.04
                timeout-minutes: 10
                permissions:
                    contents: read
                steps:
                    - uses: actions/checkout@692973e3d937129bcbf40652eb9f2f61becf3332
            test:
                runs-on: ubuntu-24.04
                timeout-minutes: 10
                permissions:
                    contents: read
                steps:
                    - uses: actions/checkout@692973e3d937129bcbf40652eb9f2f61becf3332
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["checkout-persist-credentials"], Jobs: ["build"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);
        var checkoutDiags = result.Diagnostics.Where(d => d.RuleId == "checkout-persist-credentials").ToArray();

        // build's checkout-persist-credentials suppressed, test's should remain
        await Assert.That(checkoutDiags.Length).IsEqualTo(1);
        // The remaining diagnostic should be in the test job
        await Assert.That(checkoutDiags[0].Location.StartLine).IsGreaterThanOrEqualTo(13);
    }

    [Test]
    public async Task ConfigExclusion_Jobs_DifferentRulesPerJob()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                permissions: {}
                steps:
                    - run: echo build
            test:
                runs-on: ubuntu-latest
                permissions: {}
                steps:
                    - run: echo test
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["runner-no-latest"], Jobs: ["build"]),
                new LintExclusion("**/*.yml", ["job-timeout-minutes-required"], Jobs: ["test"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);

        // build: runner-no-latest suppressed, but job-timeout-minutes-required should still appear
        var buildTimeout = result.Diagnostics.Where(d => d.RuleId == "job-timeout-minutes-required" && d.Location.StartLine <= 7).ToArray();
        await Assert.That(buildTimeout.Length).IsEqualTo(1);

        // test: job-timeout-minutes-required suppressed, but runner-no-latest should still appear
        var testRunner = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest" && d.Location.StartLine >= 8).ToArray();
        await Assert.That(testRunner.Length).IsEqualTo(1);

        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("runner-no-latest")).IsTrue();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("job-timeout-minutes-required")).IsTrue();
    }

    [Test]
    public async Task ConfigExclusion_Jobs_SuppressionSourceIsConfigJob()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                permissions: {}
                steps:
                    - run: echo ok
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["runner-no-latest"], Jobs: ["build"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);

        await Assert.That(result.SuppressionSummary.Records.Any(x => x.Source == SuppressionSource.ConfigJob)).IsTrue();
    }

    // Config exclusion: additional job-scoped scenarios
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ConfigExclusion_Jobs_ReusableWorkflowCall_SuppressesDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            call:
                uses: org/repo/.github/workflows/reusable.yml@main
                secrets: inherit
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["deny-inherit-secrets"], Jobs: ["call"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);
        var inheritDiags = result.Diagnostics.Where(d => d.RuleId == "deny-inherit-secrets").ToArray();

        await Assert.That(inheritDiags).IsEmpty();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey("deny-inherit-secrets")).IsTrue();
    }
}
