using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class OptInInformationalLintRuleTests
{
    [Test]
    public async Task AnonymousDefinition_OptInEnabled_ReportsMissingWorkflowAndJobNames()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-24.04
            steps:
              - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["anonymous-definition"] = new RuleConfig { Enabled = true },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "anonymous-definition.yml", config);

        await Assert.That(result.Diagnostics.Count(x => x.RuleId == "anonymous-definition")).IsEqualTo(2);
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "anonymous-definition" && x.Severity == DiagnosticSeverity.Info && x.Message.Contains("workflow is missing an explicit name", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "anonymous-definition" && x.Severity == DiagnosticSeverity.Info && x.Message.Contains("jobs.'build' is missing an explicit name", StringComparison.Ordinal))).IsTrue();

        var workflowDiagnostic = result.Diagnostics.Single(x => x.RuleId == "anonymous-definition" && x.Message.Contains("workflow is missing an explicit name", StringComparison.Ordinal));
        await Assert.That(workflowDiagnostic.Location.StartLine).IsEqualTo(1);
        await Assert.That(workflowDiagnostic.Location.Length).IsEqualTo(0);
        await Assert.That(workflowDiagnostic.Location.EndLine).IsEqualTo(1);
        await Assert.That(workflowDiagnostic.Location.StartColumn).IsEqualTo(workflowDiagnostic.Location.EndColumn);
    }

    [Test]
    public async Task Misfeature_OptInEnabled_ReportsSetupPythonPipInstall()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-24.04
            steps:
              - uses: actions/setup-python@v6
                with:
                  pip-install: -r requirements.txt
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["misfeature"] = new RuleConfig { Enabled = true },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "misfeature.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "misfeature" && x.Severity == DiagnosticSeverity.Info && x.Message.Contains("actions/setup-python", StringComparison.Ordinal) && x.Message.Contains("pip-install", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Misfeature_OptInEnabled_WithoutPipInstall_DoesNotReport()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-24.04
            steps:
              - uses: actions/setup-python@v6
                with:
                  python-version: '3.13'
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["misfeature"] = new RuleConfig { Enabled = true },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "misfeature-no-pip-install.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "misfeature")).IsFalse();
    }

    [Test]
    public async Task Misfeature_OptInEnabled_ActionMetadataCompositeStep_ReportsSetupPythonPipInstall()
    {
        var yaml = """
        name: Python setup helper
        description: Demo composite action
        runs:
          using: composite
          steps:
            - uses: actions/setup-python@v6
              with:
                pip-install: -r requirements.txt
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["misfeature"] = new RuleConfig { Enabled = true },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "action.yml", config);

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.DocumentKind).IsEqualTo(DocumentKind.ActionMetadata);
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "misfeature" && x.Message.Contains("pip-install", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task AnonymousDefinition_WithNames_DoesNotReport()
    {
        var yaml = """
        name: CI Pipeline
        on: push
        jobs:
          build:
            name: Build Project
            runs-on: ubuntu-24.04
            steps:
              - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["anonymous-definition"] = new RuleConfig { Enabled = true },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "anonymous-definition-named.yml", config);

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "anonymous-definition")).IsFalse();
    }

    [Test]
    public async Task AnonymousDefinition_ActionMetadata_DoesNotRun()
    {
        var yaml = """
        description: A test action
        runs:
          using: composite
          steps:
            - run: echo hello
              shell: bash
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["anonymous-definition"] = new RuleConfig { Enabled = true },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "action.yml", config);

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.DocumentKind).IsEqualTo(DocumentKind.ActionMetadata);
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "anonymous-definition")).IsFalse();
    }

    [Test]
    public async Task Misfeature_DifferentAction_WithPipInstallKey_DoesNotReport()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-24.04
            steps:
              - uses: some-org/custom-python-action@v1
                with:
                  pip-install: -r requirements.txt
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["misfeature"] = new RuleConfig { Enabled = true },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "misfeature-different-action.yml", config);

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "misfeature")).IsFalse();
    }

    [Test]
    public async Task Misfeature_RunStep_DoesNotReport()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-24.04
            steps:
              - run: pip install -r requirements.txt
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["misfeature"] = new RuleConfig { Enabled = true },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "misfeature-run-step.yml", config);

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "misfeature")).IsFalse();
    }

    [Test]
    public async Task Phase5Rules_DefaultConfig_DoNotRun()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-24.04
            steps:
              - uses: actions/setup-python@v6
                with:
                  pip-install: -r requirements.txt
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "phase5-default-disabled.yml");

        await Assert.That(result.Diagnostics.Any(x => x.RuleId is "anonymous-definition" or "misfeature")).IsFalse();
    }
}
