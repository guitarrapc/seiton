using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class OptInInformationalLintRuleTests
{
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

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "misfeature")).IsFalse();
    }
}
