using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class Phase5RuleTests
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
    public async Task SuperfluousActions_OptInEnabled_ReportsKnownReplacement()
    {
        var yaml = """
        on: push
        jobs:
          release:
            runs-on: ubuntu-24.04
            steps:
              - uses: softprops/action-gh-release@v2
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["superfluous-actions"] = new RuleConfig { Enabled = true },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "superfluous-actions.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "superfluous-actions" && x.Severity == DiagnosticSeverity.Info && x.Message.Contains("softprops/action-gh-release", StringComparison.Ordinal) && x.Message.Contains("gh release create", StringComparison.Ordinal))).IsTrue();
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
              - uses: softprops/action-gh-release@v2
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "phase5-default-disabled.yml");

        await Assert.That(result.Diagnostics.Any(x => x.RuleId is "anonymous-definition" or "misfeature" or "superfluous-actions")).IsFalse();
    }
}
