using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class LintConfigLibraryTests
{
    [Test]
    public async Task GenerateTemplateYaml_IncludesExpectedSections()
    {
        var yaml = LintConfigLibrary.GenerateTemplateYaml();
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        await Assert.That(yaml.Contains("rules:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(yaml.Contains("additiveCustomization:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(yaml.Contains("exclusions:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(yaml.Contains("exprContext:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(yaml.Contains("default_job_timeout_minutes_for_fix:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(yaml.Contains("pin_resolution:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(yaml.Contains("online_audit:", StringComparison.Ordinal)).IsTrue();

        var rulesLine = lines.FirstOrDefault(x => x.Trim() == "rules:");
        var defaultJobTimeoutLine = lines.FirstOrDefault(x => x.Trim().StartsWith("default_job_timeout_minutes_for_fix:", StringComparison.Ordinal));
        var pinResolutionLine = lines.FirstOrDefault(x => x.Trim() == "pin_resolution:");
        var pinAllowNetworkLine = lines.FirstOrDefault(x => x.Trim().StartsWith("allow_network:", StringComparison.Ordinal));
        var onlineAuditLine = lines.FirstOrDefault(x => x.Trim() == "online_audit:");
        var onlineAllowNetworkLine = lines.SkipWhile(x => x.Trim() != "online_audit:").Skip(1).FirstOrDefault(x => x.Trim().StartsWith("allow_network:", StringComparison.Ordinal));
        await Assert.That(rulesLine).IsNotNull();
        await Assert.That(defaultJobTimeoutLine).IsNotNull();
        await Assert.That(pinResolutionLine).IsNotNull();
        await Assert.That(pinAllowNetworkLine).IsNotNull();
        await Assert.That(onlineAuditLine).IsNotNull();
        await Assert.That(onlineAllowNetworkLine).IsNotNull();

        var rulesIndent = rulesLine!.Length - rulesLine.TrimStart().Length;
        var defaultTimeoutIndent = defaultJobTimeoutLine!.Length - defaultJobTimeoutLine.TrimStart().Length;
        var pinIndent = pinResolutionLine!.Length - pinResolutionLine.TrimStart().Length;
        var pinAllowNetworkIndent = pinAllowNetworkLine!.Length - pinAllowNetworkLine.TrimStart().Length;
        var onlineIndent = onlineAuditLine!.Length - onlineAuditLine.TrimStart().Length;
        var onlineAllowNetworkIndent = onlineAllowNetworkLine!.Length - onlineAllowNetworkLine.TrimStart().Length;
        await Assert.That(defaultTimeoutIndent).IsEqualTo(rulesIndent);
        await Assert.That(pinIndent).IsEqualTo(rulesIndent);
        await Assert.That(pinAllowNetworkIndent).IsEqualTo(pinIndent + 2);
        await Assert.That(onlineIndent).IsEqualTo(rulesIndent);
        await Assert.That(onlineAllowNetworkIndent).IsEqualTo(onlineIndent + 2);
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
          additionalUntrustedTriggers:
            - Issue_Comment
          additionalOutputCommands:
            - tee
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
        await Assert.That(result.Config.AdditiveCustomization.AdditionalUntrustedTriggers).HasSingleItem();
        await Assert.That(result.Config.AdditiveCustomization.AdditionalUntrustedTriggers![0]).IsEqualTo("issue_comment");
        await Assert.That(result.Config.AdditiveCustomization.AdditionalOutputCommands).HasSingleItem();
        await Assert.That(result.Config.AdditiveCustomization.AdditionalOutputCommands![0]).IsEqualTo("tee");
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

    [Test]
    public async Task Validate_PinResolution_MapsAllowNetworkAndNestedSections()
    {
        var yaml = """
        pin_resolution:
          allow_network: true
          github_actions:
            token_env_vars:
              - MY_TOKEN
            ghes_api_url: https://ghes.example.com/api/v3
            ghes_fallback: true
            ignore_actions:
              - name: "slsa-framework/.*"
                ref: ".*"
            exclude_branches:
              - release
          images:
            exclude_images:
              - alpine
            exclude_tags:
              - edge
            ignore_images:
              - ghcr.io/internal/**
          fail_open: false
          request_timeout_sec: 10
          max_concurrency: 2
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.PinResolution).IsNotNull();

        var pin = result.Config.PinResolution!;
        await Assert.That(pin.AllowNetwork).IsTrue();
        await Assert.That(pin.FailOpen).IsFalse();
        await Assert.That(pin.RequestTimeoutSec).IsEqualTo(10);
        await Assert.That(pin.MaxConcurrency).IsEqualTo(2);

        await Assert.That(pin.GitHubActions.TokenEnvVars[0]).IsEqualTo("MY_TOKEN");
        await Assert.That(pin.GitHubActions.GhesApiUrl).IsEqualTo("https://ghes.example.com/api/v3");
        await Assert.That(pin.GitHubActions.GhesFallback).IsTrue();
        await Assert.That(pin.GitHubActions.IgnoreActions.Count).IsEqualTo(1);
        await Assert.That(pin.GitHubActions.IgnoreActions[0].NamePattern).IsEqualTo("slsa-framework/.*");
        await Assert.That(pin.GitHubActions.IgnoreActions[0].RefPattern).IsEqualTo(".*");
        await Assert.That(pin.GitHubActions.ExcludeBranches).Contains("release");

        await Assert.That(pin.Images.ExcludeImages).Contains("alpine");
        await Assert.That(pin.Images.ExcludeImages).Contains("scratch");
        await Assert.That(pin.Images.ExcludeTags).Contains("edge");
        await Assert.That(pin.Images.IgnoreImages).Contains("ghcr.io/internal/**");
    }

    [Test]
    public async Task Validate_PinResolution_MapsMinAgeDays()
    {
        var yaml = """
        pin_resolution:
          allow_network: true
          github_actions:
            min_age_days: 30
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config!.PinResolution!.GitHubActions.MinAgeDays).IsEqualTo(30);
    }

    [Test]
    public async Task Validate_PinResolution_MinAgeDaysZero_DisablesAgeCheck()
    {
        var yaml = """
        pin_resolution:
          allow_network: true
          github_actions:
            min_age_days: 0
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config!.PinResolution!.GitHubActions.MinAgeDays).IsEqualTo(0);
    }

    [Test]
    public async Task Validate_PinResolution_AllowNetworkTrue_MatchesStepCompletionCondition()
    {
        var yaml = """
        pin_resolution:
          allow_network: true
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.PinResolution).IsNotNull();
        await Assert.That(result.Config.PinResolution!.AllowNetwork).IsTrue();
    }

      [Test]
      public async Task Validate_DefaultJobTimeoutMinutesForFix_MapsValue()
      {
        var yaml = """
        default_job_timeout_minutes_for_fix: 25
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.DefaultJobTimeoutMinutesForFix).IsEqualTo(25);
      }

      [Test]
      public async Task Validate_DefaultJobTimeoutMinutesForFix_InvalidValue_ReturnsError()
      {
        var yaml = """
        default_job_timeout_minutes_for_fix: abc
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("default_job_timeout_minutes_for_fix must be an integer", StringComparison.Ordinal))).IsTrue();
      }

    [Test]
    public async Task Validate_OnlineAudit_MapsAllowNetworkAndNestedSections()
    {
        var yaml = """
        online_audit:
          allow_network: true
          github_actions:
            token_env_vars:
              - MY_TOKEN
            ghes_api_url: https://ghes.example.com/api/v3
            ghes_fallback: true
            ignore_actions:
              - name: "slsa-framework/.*"
                ref: ".*"
          fail_open: false
          request_timeout_sec: 12
          max_concurrency: 3
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.OnlineAudit).IsNotNull();

        var audit = result.Config.OnlineAudit!;
        await Assert.That(audit.AllowNetwork).IsTrue();
        await Assert.That(audit.FailOpen).IsFalse();
        await Assert.That(audit.RequestTimeoutSec).IsEqualTo(12);
        await Assert.That(audit.MaxConcurrency).IsEqualTo(3);
        await Assert.That(audit.GitHubActions.TokenEnvVars[0]).IsEqualTo("MY_TOKEN");
        await Assert.That(audit.GitHubActions.GhesApiUrl).IsEqualTo("https://ghes.example.com/api/v3");
        await Assert.That(audit.GitHubActions.GhesFallback).IsTrue();
        await Assert.That(audit.GitHubActions.IgnoreActions.Count).IsEqualTo(1);
        await Assert.That(audit.GitHubActions.IgnoreActions[0].NamePattern).IsEqualTo("slsa-framework/.*");
        await Assert.That(audit.GitHubActions.IgnoreActions[0].RefPattern).IsEqualTo(".*");
    }
}
