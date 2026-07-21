using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

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
            "ok-ubuntu-26-04-preview",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-26.04
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-ubuntu-26-04-arm-preview",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-26.04-arm
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-xcode-27-preview",
            """
            on: push
            jobs:
                build:
                    runs-on: xcode-27
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-xcode-27-xlarge-preview",
            """
            on: push
            jobs:
                build:
                    runs-on: xcode-27-xlarge
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
    public async Task RunnerLabelRule_EmptyLabel_NoDuplicateWithParser()
    {
        // Parser reports "runs-on label should not be empty" (syntax-check).
        // RunnerLabelRule should NOT also report the empty label as unknown.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ''
            steps:
              - uses: actions/checkout@0ad4b8fadaa221de15dcec353f45205ec38ea70b
        """;

        var engine = new LintEngine([new RunnerLabelRule()]);
        using var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "empty-label.yml");

        var runnerLabelDiags = result.Diagnostics.Where(d => d.RuleId == "runner-label").ToArray();
        await Assert.That(runnerLabelDiags.Length).IsEqualTo(0)
            .Because("Empty labels are already reported by the parser as syntax-check; runner-label should not duplicate");
    }


    [Test]
    public async Task RunnerLabelRule_UnknownLabel_ListsAllAvailableLabels()
    {
        // The "unknown label" message should categorize labels by type.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: nonexistent-runner
            steps:
              - uses: actions/checkout@0ad4b8fadaa221de15dcec353f45205ec38ea70b
        """;

        var engine = new LintEngine([new RunnerLabelRule()]);
        using var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "unknown-label.yml");

        var runnerLabelDiag = result.Diagnostics.First(d => d.RuleId == "runner-label");
        // Must contain categorized sections
        await Assert.That(runnerLabelDiag.Message).Contains("hosted runners:");
        await Assert.That(runnerLabelDiag.Message).Contains("larger runners:");
        await Assert.That(runnerLabelDiag.Message).Contains("self-hosted presets:");
        // Hosted runners section should contain standard labels
        await Assert.That(runnerLabelDiag.Message).Contains("\"ubuntu-latest\"");
        await Assert.That(runnerLabelDiag.Message).Contains("\"windows-latest\"");
        // Larger runners section should contain larger labels
        await Assert.That(runnerLabelDiag.Message).Contains("\"macos-latest-xlarge\"");
        await Assert.That(runnerLabelDiag.Message).Contains("\"ubuntu-latest-4-cores\"");
        // Self-hosted presets section should contain self-hosted labels
        await Assert.That(runnerLabelDiag.Message).Contains("\"self-hosted\"");
    }


    [Test]
    public async Task RunnerLabelRule_UnknownLabel_WithCustomLabels_ShowsCustomSection()
    {
        // When custom labels are configured, the message should show them in a separate section.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: nonexistent-runner
            steps:
              - uses: actions/checkout@0ad4b8fadaa221de15dcec353f45205ec38ea70b
        """;

        var engine = new LintEngine([new RunnerLabelRule()]);
        using var result = engine.Check(

            Encoding.UTF8.GetBytes(yaml),
            "custom-label.yml",
            new LintConfig
            {
                Rules = new Dictionary<string, RuleConfig>
                {
                    ["runner-label"] = new RuleConfig { KnownHostedLabels = (string[])["my-custom-runner", "team-gpu"] },
                },
            });

        var runnerLabelDiag = result.Diagnostics.First(d => d.RuleId == "runner-label");
        await Assert.That(runnerLabelDiag.Message).Contains("custom labels:");
        await Assert.That(runnerLabelDiag.Message).Contains("\"my-custom-runner\"");
        await Assert.That(runnerLabelDiag.Message).Contains("\"team-gpu\"");
    }


    [Test]
    public async Task RunnerLabelRule_LargerRunnerLabels_NotReportedAsUnknown()
    {
        // GitHub larger runners (macOS from docs + Ubuntu/Windows supplemental) should be known labels.
        var cases = new[]
        {
            "macos-latest-xlarge",
            "macos-latest-large",
            "macos-15-xlarge",
            "macos-15-large",
            "macos-14-xlarge",
            "macos-14-large",
            "macos-26-xlarge",
            "macos-26-large",
            // Supplemental larger runners (not in docs, from blog announcement)
            "ubuntu-latest-4-cores",
            "ubuntu-latest-8-cores",
            "ubuntu-latest-16-cores",
            "windows-latest-8-cores",
            // Supplemental preview runners (not in docs yet)
            "xcode-27-xlarge",
        };

        var engine = new LintEngine([new RunnerLabelRule()]);
        foreach (var label in cases)
        {
            var yaml = $"""
            on: push
            jobs:
              build:
                runs-on: {label}
                steps:
                  - uses: actions/checkout@0ad4b8fadaa221de15dcec353f45205ec38ea70b
            """;
            using var result = engine.Check(Encoding.UTF8.GetBytes(yaml), $"larger-runner-{label}.yml");
            var diags = result.Diagnostics.Where(d => d.RuleId == "runner-label").ToArray();
            await Assert.That(diags.Length).IsEqualTo(0)
                .Because($"'{label}' is a GitHub larger runner label and should not be reported as unknown");
        }
    }


    [Test]
    public async Task RunnerLabelRule_SelfHostedPresetLabel_NotReportedAsUnknown()
    {
        // Self-hosted preset labels (x64, arm, arm64, linux, macos, windows) should not
        // be flagged as unknown even without "self-hosted" in the array.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: x64
            steps:
              - uses: actions/checkout@0ad4b8fadaa221de15dcec353f45205ec38ea70b
        """;

        var engine = new LintEngine([new RunnerLabelRule()]);
        using var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "preset-label.yml");

        var runnerLabelDiags = result.Diagnostics.Where(d => d.RuleId == "runner-label").ToArray();
        await Assert.That(runnerLabelDiags.Length).IsEqualTo(0)
            .Because("x64 is a self-hosted preset label and should not be reported as unknown");
    }
}
