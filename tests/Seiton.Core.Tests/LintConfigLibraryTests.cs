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
        await Assert.That(yaml.Contains("exclusions:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(yaml.Contains("fix:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(yaml.Contains("network:", StringComparison.Ordinal)).IsTrue();

        var rulesLine = lines.FirstOrDefault(x => x.Trim() == "rules:");
        var fixLine = lines.FirstOrDefault(x => x.Trim() == "fix:");
        var networkLine = lines.FirstOrDefault(x => x.Trim() == "network:");
        await Assert.That(rulesLine).IsNotNull();
        await Assert.That(fixLine).IsNotNull();
        await Assert.That(networkLine).IsNotNull();

        var rulesIndent = rulesLine!.Length - rulesLine.TrimStart().Length;
        var fixIndent = fixLine!.Length - fixLine.TrimStart().Length;
        var networkIndent = networkLine!.Length - networkLine.TrimStart().Length;
        await Assert.That(fixIndent).IsEqualTo(rulesIndent);
        await Assert.That(networkIndent).IsEqualTo(rulesIndent);
    }

    [Test]
    public async Task Validate_ValidConfig_NormalizesAndReturnsConfig()
    {
        var yaml = """
        rules:
          dangerous-triggers:
            enabled: true
            severity: warning
            events:
              extend:
                - Workflow_Run
                - workflow_run
          runner-label:
            known-hosted-labels:
              extend:
                - Ubuntu-24.04-Large
          credentials:
            public-registries:
              extend:
                - GHCR.IO
          cache-poisoning:
            untrusted-triggers:
              extend:
                - Issue_Comment
          unredacted-secrets:
            output-commands:
              extend:
                - tee
        exclusions:
          -
            files: .github/workflows/legacy-*.yml
            rules:
              - runner-label
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Config!.Rules).ContainsKey("dangerous-triggers");
        var dtConfig = result.Config.Rules!["dangerous-triggers"];
        await Assert.That(dtConfig.Events).IsNotNull();
        await Assert.That(dtConfig.Events!.Extend).HasSingleItem();
        await Assert.That(dtConfig.Events.Extend[0]).IsEqualTo("workflow_run");

        var credConfig = result.Config.Rules["credentials"];
        await Assert.That(credConfig.PublicRegistries).IsNotNull();
        await Assert.That(credConfig.PublicRegistries!.Extend[0]).IsEqualTo("ghcr.io");

        var cpConfig = result.Config.Rules["cache-poisoning"];
        await Assert.That(cpConfig.UntrustedTriggers).IsNotNull();
        await Assert.That(cpConfig.UntrustedTriggers!.Extend).HasSingleItem();
        await Assert.That(cpConfig.UntrustedTriggers.Extend[0]).IsEqualTo("issue_comment");

        var usConfig = result.Config.Rules["unredacted-secrets"];
        await Assert.That(usConfig.OutputCommands).IsNotNull();
        await Assert.That(usConfig.OutputCommands!.Extend).HasSingleItem();
        await Assert.That(usConfig.OutputCommands.Extend[0]).IsEqualTo("tee");
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
        rules:
          credentials:
            public-registries:
              extend:
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
    public async Task Validate_Fix_MapsAllSections()
    {
        var yaml = """
        fix:
          defaults:
            job-timeout-minutes: 25
          pinning:
            enable-network: true
            min-age-days: 30
            exclude-branches:
              - release
            ignore-actions:
              - uses: "slsa-framework/.*"
                ref: ".*"
          images:
            enable-network: true
            exclude-images:
              - alpine
            exclude-tags:
              - edge
            ignore-images:
              - ghcr.io/internal/**
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();

        var fix = result.Config!.Fix;
        await Assert.That(fix.Defaults.JobTimeoutMinutes).IsEqualTo(25);
        await Assert.That(fix.Pinning.EnableNetwork).IsTrue();
        await Assert.That(fix.Pinning.MinAgeDays).IsEqualTo(30);
        await Assert.That(fix.Pinning.ExcludeBranches).Contains("release");
        await Assert.That(fix.Pinning.IgnoreActions.Count).IsEqualTo(1);
        await Assert.That(fix.Pinning.IgnoreActions[0].NamePattern).IsEqualTo("slsa-framework/.*");
        await Assert.That(fix.Pinning.IgnoreActions[0].RefPattern).IsEqualTo(".*");

        await Assert.That(fix.Images.EnableNetwork).IsTrue();
        await Assert.That(fix.Images.ExcludeImages).Contains("alpine");
        await Assert.That(fix.Images.ExcludeImages).Contains("scratch");
        await Assert.That(fix.Images.ExcludeTags).Contains("edge");
        await Assert.That(fix.Images.IgnoreImages).Contains("ghcr.io/internal/**");
    }

    [Test]
    public async Task Validate_Fix_MinAgeDaysZero_DisablesAgeCheck()
    {
        var yaml = """
        fix:
          pinning:
            min-age-days: 0
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config!.Fix.Pinning.MinAgeDays).IsEqualTo(0);
    }

    [Test]
    public async Task Validate_Network_MapsAllFields()
    {
        var yaml = """
        network:
          on-error: fail
          timeout-seconds: 10
          max-concurrency: 2
          github:
            ghes-api-url: https://ghes.example.com/api/v3
            ghes-fallback: true
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();

        var network = result.Config!.Network;
        await Assert.That(network.OnError).IsEqualTo(NetworkErrorMode.Fail);
        await Assert.That(network.TimeoutSeconds).IsEqualTo(10);
        await Assert.That(network.MaxConcurrency).IsEqualTo(2);
        await Assert.That(network.GitHub.GhesApiUrl).IsEqualTo("https://ghes.example.com/api/v3");
        await Assert.That(network.GitHub.GhesFallback).IsTrue();
    }

    [Test]
    public async Task Validate_Network_InvalidTimeoutSeconds_ReturnsError()
    {
        var yaml = """
        network:
          timeout-seconds: -5
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("timeout-seconds must be >= 0", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_Network_InvalidMaxConcurrency_ReturnsError()
    {
        var yaml = """
        network:
          max-concurrency: 0
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("max-concurrency must be > 0", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_RuleSpecificExtendKeys_ParseCorrectly()
    {
        var yaml = """
        rules:
          expr-undefined-var:
            assume-events:
              - workflow_dispatch
          forbidden-uses:
            allow:
              - actions/*
            deny:
              - some-org/*
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();

        var exprConfig = result.Config!.Rules!["expr-undefined-var"];
        await Assert.That(exprConfig.AssumeEvents).IsNotNull();
        await Assert.That(exprConfig.AssumeEvents![0]).IsEqualTo("workflow_dispatch");

        var forbiddenConfig = result.Config.Rules["forbidden-uses"];
        await Assert.That(forbiddenConfig.Allow).IsNotNull();
        await Assert.That(forbiddenConfig.Allow![0]).IsEqualTo("actions/*");
        await Assert.That(forbiddenConfig.Deny).IsNotNull();
        await Assert.That(forbiddenConfig.Deny![0]).IsEqualTo("some-org/*");
    }

    [Test]
    public async Task Validate_Exclusions_NewFieldNames_ParseCorrectly()
    {
        var yaml = """
        exclusions:
          -
            files: .github/workflows/release.yml
            rules:
              - credentials
            jobs:
              - publish
              - deploy
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.Exclusions).Count().IsEqualTo(1);

        var excl = result.Config.Exclusions![0];
        await Assert.That(excl.Files).IsEqualTo(".github/workflows/release.yml");
        await Assert.That(excl.Rules).Contains("credentials");
        await Assert.That(excl.Jobs).IsNotNull();
        await Assert.That(excl.Jobs!.Count).IsEqualTo(2);
        await Assert.That(excl.Jobs[0]).IsEqualTo("publish");
        await Assert.That(excl.Jobs[1]).IsEqualTo("deploy");
    }

    [Test]
    public async Task Validate_Exclusions_InlineFormat_ParsesCorrectly()
    {
        var yaml = """
        exclusions:
          - files: ".github/workflows/legacy-*.yml"
            rules:
              - runner-no-latest
              - job-permissions-required
          - files: ".github/workflows/release.yml"
            jobs:
              - publish
            rules:
              - credentials
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.Exclusions!.Count).IsEqualTo(2);

        var excl0 = result.Config.Exclusions![0];
        await Assert.That(excl0.Files).IsEqualTo(".github/workflows/legacy-*.yml");
        await Assert.That(excl0.Rules).Contains("runner-no-latest");
        await Assert.That(excl0.Rules).Contains("job-permissions-required");

        var excl1 = result.Config.Exclusions![1];
        await Assert.That(excl1.Files).IsEqualTo(".github/workflows/release.yml");
        await Assert.That(excl1.Rules).Contains("credentials");
        await Assert.That(excl1.Jobs).IsNotNull();
        await Assert.That(excl1.Jobs![0]).IsEqualTo("publish");
    }

    [Test]
    public async Task Validate_RuleSpecificKey_WrongRule_ReturnsError()
    {
        var yaml = """
        rules:
          runner-label:
            events:
              extend:
                - issue_comment
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("does not accept 'events'", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_RuleSpecificKey_CorrectRule_Accepted()
    {
        var yaml = """
        rules:
          dangerous-triggers:
            events:
              extend:
                - issue_comment
          runner-label:
            known-hosted-labels:
              extend:
                - ubuntu-24.04-large
          credentials:
            public-registries:
              extend:
                - ghcr.io
          cache-poisoning:
            untrusted-triggers:
              extend:
                - issue_comment
          self-hosted-runner:
            untrusted-triggers:
              extend:
                - issue_comment
          unredacted-secrets:
            output-commands:
              extend:
                - tee
          expr-undefined-var:
            assume-events:
              - workflow_dispatch
          forbidden-uses:
            allow:
              - actions/*
            deny:
              - some-org/*
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Validate_FullTargetConfig_ParsesAllSections()
    {
        var yaml = """
        rules:
          job-permissions-required:
            enabled: false
          deny-write-all:
            severity: error
          dangerous-triggers:
            severity: error
            events:
              extend:
                - issue_comment
          runner-label:
            known-hosted-labels:
              extend:
                - ubuntu-24.04-large
          credentials:
            public-registries:
              extend:
                - registry.example.com
          cache-poisoning:
            untrusted-triggers:
              extend:
                - issue_comment
          unredacted-secrets:
            output-commands:
              extend:
                - tee
          forbidden-uses:
            deny:
              - some-untrusted-org/*
          expr-undefined-var:
            assume-events:
              - workflow_dispatch
              - repository_dispatch
          known-vulnerable-actions:
            enabled: true
          impostor-commit:
            enabled: true

        exclusions:
          - files: ".github/workflows/legacy-*.yml"
            rules:
              - runner-no-latest
              - job-permissions-required
          - files: ".github/workflows/release.yml"
            jobs:
              - publish
            rules:
              - credentials

        fix:
          defaults:
            job-timeout-minutes: 15
          pinning:
            enable-network: true
            min-age-days: 14
            exclude-branches:
              - main
              - master
            ignore-actions:
              - uses: "slsa-framework/.*"
                ref: ".*"
          images:
            enable-network: true
            exclude-images:
              - scratch
            exclude-tags:
              - latest

        network:
          on-error: skip
          timeout-seconds: 30
          max-concurrency: 4
          github:
            ghes-api-url: ""
            ghes-fallback: false
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();

        // rules
        await Assert.That(result.Config!.Rules!["job-permissions-required"].Enabled).IsFalse();
        await Assert.That(result.Config.Rules["deny-write-all"].Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(result.Config.Rules["dangerous-triggers"].Events!.Extend[0]).IsEqualTo("issue_comment");
        await Assert.That(result.Config.Rules["runner-label"].KnownHostedLabels!.Extend[0]).IsEqualTo("ubuntu-24.04-large");
        await Assert.That(result.Config.Rules["credentials"].PublicRegistries!.Extend[0]).IsEqualTo("registry.example.com");
        await Assert.That(result.Config.Rules["cache-poisoning"].UntrustedTriggers!.Extend[0]).IsEqualTo("issue_comment");
        await Assert.That(result.Config.Rules["unredacted-secrets"].OutputCommands!.Extend[0]).IsEqualTo("tee");
        await Assert.That(result.Config.Rules["forbidden-uses"].Deny![0]).IsEqualTo("some-untrusted-org/*");
        await Assert.That(result.Config.Rules["expr-undefined-var"].AssumeEvents!.Count).IsEqualTo(2);
        await Assert.That(result.Config.Rules["known-vulnerable-actions"].Enabled).IsTrue();
        await Assert.That(result.Config.Rules["impostor-commit"].Enabled).IsTrue();

        // exclusions (inline format)
        await Assert.That(result.Config.Exclusions!.Count).IsEqualTo(2);
        await Assert.That(result.Config.Exclusions[0].Files).IsEqualTo(".github/workflows/legacy-*.yml");
        await Assert.That(result.Config.Exclusions[1].Jobs![0]).IsEqualTo("publish");

        // fix
        await Assert.That(result.Config.Fix.Defaults.JobTimeoutMinutes).IsEqualTo(15);
        await Assert.That(result.Config.Fix.Pinning.EnableNetwork).IsTrue();
        await Assert.That(result.Config.Fix.Pinning.MinAgeDays).IsEqualTo(14);
        await Assert.That(result.Config.Fix.Images.EnableNetwork).IsTrue();

        // network
        await Assert.That(result.Config.Network.OnError).IsEqualTo(NetworkErrorMode.Skip);
        await Assert.That(result.Config.Network.TimeoutSeconds).IsEqualTo(30);
        await Assert.That(result.Config.Network.MaxConcurrency).IsEqualTo(4);
    }

    [Test]
    public async Task Validate_OldTopLevelKeys_ReturnsUnknownKeyError()
    {
        var yaml = """
        additiveCustomization:
          additionalDangerousEvents:
            - issue_comment
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown top-level key 'additiveCustomization'", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_MultipleInvalidRuleKeys_ReportsAllErrors()
    {
        var yaml = """
        rules:
          runner-label:
            events:
              extend:
                - issue_comment
            deny:
              - some-org/*
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Count(x => x.Message.Contains("does not accept", StringComparison.Ordinal))).IsEqualTo(2);
    }

    [Test]
    public async Task Validate_RuleSpecificConfig_ProjectsTypedFields()
    {
        var yaml = """
        rules:
          dangerous-triggers:
            events:
              extend:
                - issue_comment
          forbidden-uses:
            allow:
              - actions/*
            deny:
              - bad-org/*
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();

        var dangerous = result.Config!.Rules!["dangerous-triggers"];
        await Assert.That(dangerous.Events).IsNotNull();
        await Assert.That(dangerous.Events!.Extend[0]).IsEqualTo("issue_comment");

        var forbidden = result.Config.Rules["forbidden-uses"];
        await Assert.That(forbidden.Allow).IsNotNull();
        await Assert.That(forbidden.Allow![0]).IsEqualTo("actions/*");
        await Assert.That(forbidden.Deny).IsNotNull();
        await Assert.That(forbidden.Deny![0]).IsEqualTo("bad-org/*");
    }
}
