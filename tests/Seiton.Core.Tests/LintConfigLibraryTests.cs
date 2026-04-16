using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class LintConfigLibraryTests
{
    [Test]
    public async Task GenerateTemplateYaml_IncludesExpectedSections()
    {
        var yaml = LintConfigLibrary.GenerateTemplateYaml();

        await Assert.That(yaml.Contains("rules:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(yaml.Contains("additiveCustomization:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(yaml.Contains("exclusions:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(yaml.Contains("exprContext:", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Validate_ValidConfig_NormalizesAndReturnsConfig()
    {
        var yaml = """
        rules:
          dangerous-triggers:
            enabled: true
            severity: warning
        additiveCustomization:
          additionalDangerousEvents:
            - Workflow_Run
            - workflow_run
          additionalKnownHostedLabels:
            - Ubuntu-24.04-Large
          additionalPublicRegistries:
            - GHCR.IO
        exclusions:
          -
            filePattern: .github/workflows/legacy-*.yml
            ruleIds:
              - runner-label
        exprContext:
          eventTypes:
            - workflow_dispatch
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Config!.RuleOptions).ContainsKey("dangerous-triggers");
        await Assert.That(result.Config.AdditiveCustomization.AdditionalDangerousEvents).HasSingleItem();
        await Assert.That(result.Config.AdditiveCustomization.AdditionalDangerousEvents![0]).IsEqualTo("workflow_run");
        await Assert.That(result.Config.AdditiveCustomization.AdditionalPublicRegistries![0]).IsEqualTo("ghcr.io");
    }

    [Test]
    public async Task Validate_UnknownRuleId_ReturnsError()
    {
        var yaml = """
        rules:
          runner-lable:
            enabled: false
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error && x.Message.Contains("unknown rule-id 'runner-lable'", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_InvalidRegistryHost_ReturnsError()
    {
        var yaml = """
        additiveCustomization:
          additionalPublicRegistries:
            - https://ghcr.io
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("additional public registry host", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task FindRecommendedConfigPath_PicksPreferredPathOrder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "seiton-config-test-" + Guid.NewGuid().ToString("N"));
        var githubDir = Path.Combine(tempRoot, ".github");

        try
        {
            Directory.CreateDirectory(githubDir);
            File.WriteAllText(Path.Combine(tempRoot, "seiton.yaml"), "rules: {}\n");
            File.WriteAllText(Path.Combine(githubDir, "seiton.yml"), "rules: {}\n");
            File.WriteAllText(Path.Combine(githubDir, "seiton.yaml"), "rules: {}\n");

            var found = LintConfigLibrary.FindRecommendedConfigPath(tempRoot);

            await Assert.That(found).IsEqualTo(Path.Combine(githubDir, "seiton.yaml"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
