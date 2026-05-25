using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{
    [Test]
    public async Task RuleRegression_ActionShellIsRequiredRule_ActionMetadataRunWithoutShell_ReportsDiagnostic()
    {
        var yaml = NormalizeYaml(
            """
            name: Sample action
            runs:
                using: composite
                steps:
                    - run: echo hello
            """);

        using var result = new LintEngine([new ActionShellIsRequiredRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "action.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "action-shell-is-required").ToArray();

        await Assert.That(result.DocumentKind).IsEqualTo(DocumentKind.ActionMetadata);
        await Assert.That(diagnostics).Count().IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).IsEqualTo("shell is required if run is set");
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(5);
    }

    [Test]
    public async Task RuleRegression_ActionShellIsRequiredRule_ActionMetadataRunWithShell_HasNoDiagnostic()
    {
        var yaml = NormalizeYaml(
            """
            name: Sample action
            runs:
                using: composite
                steps:
                    - run: echo hello
                      shell: bash
            """);

        using var result = new LintEngine([new ActionShellIsRequiredRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), ".github/actions/sample/action.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "action-shell-is-required").ToArray();

        await Assert.That(result.DocumentKind).IsEqualTo(DocumentKind.ActionMetadata);
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RuleRegression_ActionShellIsRequiredRule_WorkflowInputs_NoDiagnostics()
    {
        var cases = new[]
        {
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
            "ok-workflow-step-no-run",
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
}
