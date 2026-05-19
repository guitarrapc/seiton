using System.Text;
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
        await Assert.That(yaml.Contains("output:", StringComparison.Ordinal)).IsTrue();

        var rulesLine = lines.FirstOrDefault(x => x.Trim() == "rules:");
        var fixLine = lines.FirstOrDefault(x => x.Trim() == "fix:");
        var networkLine = lines.FirstOrDefault(x => x.Trim() == "network:");
        var outputLine = lines.FirstOrDefault(x => x.Trim() == "output:");
        await Assert.That(rulesLine).IsNotNull();
        await Assert.That(fixLine).IsNotNull();
        await Assert.That(networkLine).IsNotNull();
        await Assert.That(outputLine).IsNotNull();

        var rulesIndent = rulesLine!.Length - rulesLine.TrimStart().Length;
        var fixIndent = fixLine!.Length - fixLine.TrimStart().Length;
        var networkIndent = networkLine!.Length - networkLine.TrimStart().Length;
        var outputIndent = outputLine!.Length - outputLine.TrimStart().Length;
        await Assert.That(fixIndent).IsEqualTo(rulesIndent);
        await Assert.That(networkIndent).IsEqualTo(rulesIndent);
        await Assert.That(outputIndent).IsEqualTo(rulesIndent);
    }

    [Test]
    public async Task Validate_Utf8YamlBytesMatchInputUtf8Encoding()
    {
        var yaml = """
        rules:
          dangerous-triggers:
            enabled: false
        """;

        var expectedUtf8 = Encoding.UTF8.GetBytes(yaml);
        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.Utf8Yaml.AsSpan().SequenceEqual(expectedUtf8)).IsTrue();
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
            file: .github/workflows/legacy-*.yml
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
              - uses: "slsa-framework/*"
                ref: "*"
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
        await Assert.That(fix.Pinning.IgnoreActions[0].NamePattern).IsEqualTo("slsa-framework/*");
        await Assert.That(fix.Pinning.IgnoreActions[0].RefPattern).IsEqualTo("*");

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
    public async Task Validate_Network_GhesApiUrl_Http_ReturnsError_AndClearsUrl()
    {
        var yaml = """
        network:
          github:
            ghes-api-url: http://ghes.example.com/api/v3
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.Network.GitHub.GhesApiUrl).IsNull();
        await Assert.That(result.Diagnostics.Any(x =>
            x.Severity == DiagnosticSeverity.Error
            && x.Message.Contains("https scheme", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_Network_GhesApiUrl_WithUserInfo_ReturnsError()
    {
        var yaml = """
        network:
          github:
            ghes-api-url: https://user:pass@ghes.example.com/api/v3
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Config!.Network.GitHub.GhesApiUrl).IsNull();
        await Assert.That(result.Diagnostics.Any(x =>
            x.Severity == DiagnosticSeverity.Error
            && x.Message.Contains("credentials", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_Network_GhesApiUrl_NonAbsolute_ReturnsError()
    {
        var yaml = """
        network:
          github:
            ghes-api-url: ghes.example.com/api/v3
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Config!.Network.GitHub.GhesApiUrl).IsNull();
        await Assert.That(result.Diagnostics.Any(x =>
            x.Severity == DiagnosticSeverity.Error
            && x.Message.Contains("absolute https URL", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Config!.Network.MaxConcurrency).IsEqualTo(LintConfigResourceLimits.DefaultNetworkMaxConcurrency);
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
    public async Task Validate_UnpinnedUsesIgnoreActions_StringForm_Error()
    {
        var yaml = """
        rules:
          unpinned-uses:
            ignore-actions:
              - guitarrapc/setup-dotnet
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("mapping with owner", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_UnpinnedUsesIgnoreActions_ObjectForm_OmittedRefs_IgnoresAllRefs()
    {
        var yaml = """
        rules:
          unpinned-uses:
            ignore-actions:
              - owner: "my-org/*"
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();

        var unpinnedConfig = result.Config!.Rules!["unpinned-uses"];
        await Assert.That(unpinnedConfig.IgnoreActions).IsNotNull();
        await Assert.That(unpinnedConfig.IgnoreActions!.Count).IsEqualTo(1);
        await Assert.That(unpinnedConfig.IgnoreActions![0].Pattern).IsEqualTo("my-org/*");
        await Assert.That(unpinnedConfig.IgnoreActions![0].Refs).IsNull();
    }

    [Test]
    public async Task Validate_UnpinnedUsesIgnoreActions_ObjectForm_ParseCorrectly()
    {
        var yaml = """
        rules:
          unpinned-uses:
            ignore-actions:
              - owner: "my-org/*"
                refs: [main, master]
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();

        var unpinnedConfig = result.Config!.Rules!["unpinned-uses"];
        await Assert.That(unpinnedConfig.IgnoreActions).IsNotNull();
        await Assert.That(unpinnedConfig.IgnoreActions!.Count).IsEqualTo(1);
        await Assert.That(unpinnedConfig.IgnoreActions![0].Pattern).IsEqualTo("my-org/*");
        await Assert.That(unpinnedConfig.IgnoreActions![0].Refs).IsNotNull();
        await Assert.That(unpinnedConfig.IgnoreActions![0].Refs!.Count).IsEqualTo(2);
        await Assert.That(unpinnedConfig.IgnoreActions![0].Refs![0]).IsEqualTo("main");
        await Assert.That(unpinnedConfig.IgnoreActions![0].Refs![1]).IsEqualTo("master");
    }

    [Test]
    public async Task Validate_UnpinnedUsesIgnoreActions_ObjectForms_ParseCorrectly()
    {
        var yaml = """
        rules:
          unpinned-uses:
            ignore-actions:
              - owner: "trusted-org/*"
              - owner: "semi-trusted/*"
                refs: [main]
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();

        var unpinnedConfig = result.Config!.Rules!["unpinned-uses"];
        await Assert.That(unpinnedConfig.IgnoreActions).IsNotNull();
        await Assert.That(unpinnedConfig.IgnoreActions!.Count).IsEqualTo(2);
        await Assert.That(unpinnedConfig.IgnoreActions![0].Pattern).IsEqualTo("trusted-org/*");
        await Assert.That(unpinnedConfig.IgnoreActions![0].Refs).IsNull();
        await Assert.That(unpinnedConfig.IgnoreActions![1].Pattern).IsEqualTo("semi-trusted/*");
        await Assert.That(unpinnedConfig.IgnoreActions![1].Refs).IsNotNull();
        await Assert.That(unpinnedConfig.IgnoreActions![1].Refs![0]).IsEqualTo("main");
    }

    [Test]
    public async Task Validate_UnpinnedUsesIgnoreActions_ObjectForm_MissingOwner_Error()
    {
        var yaml = """
        rules:
          unpinned-uses:
            ignore-actions:
              - refs: [main]
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("requires 'owner' key", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_UnpinnedUsesIgnoreActions_ObjectForm_EmptyRefs_Error()
    {
        var yaml = """
        rules:
          unpinned-uses:
            ignore-actions:
              - owner: "my-org/*"
                refs: []
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("non-empty 'refs' list", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_UnpinnedUsesIgnoreActions_ObjectForm_UnknownKey_Error()
    {
        var yaml = """
        rules:
          unpinned-uses:
            ignore-actions:
              - owner: "my-org/*"
                refs: [main]
                unknown-key: true
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("unknown ignore-actions key", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_UnpinnedUsesIgnoreActions_ObjectForm_WhitespaceOnlyRef_Error()
    {
        var yaml = """
        rules:
          unpinned-uses:
            ignore-actions:
              - owner: "my-org/*"
                refs: ["   "]
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("ref entries must not be empty", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_UnpinnedUsesIgnoreActions_NormalizesPatternAndRefs()
    {
        var yaml = """
        rules:
          unpinned-uses:
            ignore-actions:
              - owner: " My-Org/* "
                refs: [" main ", main, " master ", main]
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();

        var unpinnedConfig = result.Config!.Rules!["unpinned-uses"];
        await Assert.That(unpinnedConfig.IgnoreActions).IsNotNull();
        await Assert.That(unpinnedConfig.IgnoreActions!.Count).IsEqualTo(1);
        await Assert.That(unpinnedConfig.IgnoreActions![0].Pattern).IsEqualTo("my-org/*");
        await Assert.That(unpinnedConfig.IgnoreActions![0].Refs).IsNotNull();
        await Assert.That(unpinnedConfig.IgnoreActions![0].Refs!.Count).IsEqualTo(2);
        await Assert.That(unpinnedConfig.IgnoreActions![0].Refs![0]).IsEqualTo("main");
        await Assert.That(unpinnedConfig.IgnoreActions![0].Refs![1]).IsEqualTo("master");
    }

    [Test]
    public async Task Validate_Exclusions_NewFieldNames_ParseCorrectly()
    {
        var yaml = """
        exclusions:
          -
            file: .github/workflows/release.yml
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
        await Assert.That(excl.File).IsEqualTo(".github/workflows/release.yml");
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
          - file: ".github/workflows/legacy-*.yml"
            rules:
              - runner-no-latest
              - job-permissions-required
          - file: ".github/workflows/release.yml"
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
        await Assert.That(excl0.File).IsEqualTo(".github/workflows/legacy-*.yml");
        await Assert.That(excl0.Rules).Contains("runner-no-latest");
        await Assert.That(excl0.Rules).Contains("job-permissions-required");

        var excl1 = result.Config.Exclusions![1];
        await Assert.That(excl1.File).IsEqualTo(".github/workflows/release.yml");
        await Assert.That(excl1.Rules).Contains("credentials");
        await Assert.That(excl1.Jobs).IsNotNull();
        await Assert.That(excl1.Jobs![0]).IsEqualTo("publish");
    }

    [Test]
    public async Task Validate_Exclusions_FileKey_Singular_ParsesCorrectly()
    {
        var yaml = """
        exclusions:
          - file: ".github/workflows/legacy-*.yml"
            rules:
              - runner-no-latest
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.Exclusions!.Count).IsEqualTo(1);
        await Assert.That(result.Config.Exclusions![0].File).IsEqualTo(".github/workflows/legacy-*.yml");
    }

    [Test]
    public async Task Validate_Exclusions_FileOnly_NoRules_ExcludesAllRules()
    {
        // When only 'file:' is specified without 'rules:', the exclusion applies to all rules (file-level exclusion)
        var yaml = """
        exclusions:
          - file: .github/workflows/legacy-*.yml
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.Exclusions!.Count).IsEqualTo(1);
        var excl = result.Config.Exclusions![0];
        await Assert.That(excl.File).IsEqualTo(".github/workflows/legacy-*.yml");
        await Assert.That(excl.Rules).IsNull(); // null = all rules
        await Assert.That(excl.Jobs).IsNull();
    }

    [Test]
    public async Task Validate_Exclusions_FileAndJobs_NoRules_ExcludesAllRulesForJobs()
    {
        // file + jobs without rules → exclude all rules for those jobs
        var yaml = """
        exclusions:
          - file: .github/workflows/legacy-*.yml
            jobs:
              - build
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.Exclusions!.Count).IsEqualTo(1);
        var excl = result.Config.Exclusions![0];
        await Assert.That(excl.File).IsEqualTo(".github/workflows/legacy-*.yml");
        await Assert.That(excl.Rules).IsNull(); // null = all rules
        await Assert.That(excl.Jobs).IsNotNull();
        await Assert.That(excl.Jobs![0]).IsEqualTo("build");
    }

    [Test]
    public async Task Validate_Exclusions_EmptyRulesList_ExcludesNothing()
    {
        // Explicit empty rules list is a no-op (distinct from omitted)
        var yaml = """
        exclusions:
          - file: .github/workflows/legacy-*.yml
            rules: []
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
        await Assert.That(result.Config!.Exclusions!.Count).IsEqualTo(0);
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
          - file: ".github/workflows/legacy-*.yml"
            rules:
              - runner-no-latest
              - job-permissions-required
          - file: ".github/workflows/release.yml"
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
              - uses: "slsa-framework/*"
                ref: "*"
          images:
            enable-network: true
            exclude-images:
              - scratch
            exclude-tags:
              - latest

        network:
          on-error: skip
          timeout-seconds: 30
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
        await Assert.That(result.Config.Exclusions[0].File).IsEqualTo(".github/workflows/legacy-*.yml");
        await Assert.That(result.Config.Exclusions[1].Jobs![0]).IsEqualTo("publish");

        // fix
        await Assert.That(result.Config.Fix.Defaults.JobTimeoutMinutes).IsEqualTo(15);
        await Assert.That(result.Config.Fix.Pinning.EnableNetwork).IsTrue();
        await Assert.That(result.Config.Fix.Pinning.MinAgeDays).IsEqualTo(14);
        await Assert.That(result.Config.Fix.Images.EnableNetwork).IsTrue();

        // network
        await Assert.That(result.Config.Network.OnError).IsEqualTo(NetworkErrorMode.Skip);
        await Assert.That(result.Config.Network.TimeoutSeconds).IsEqualTo(30);
        await Assert.That(result.Config.Network.MaxConcurrency).IsEqualTo(LintConfigResourceLimits.DefaultNetworkMaxConcurrency);
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

    [Test]
    public async Task Validate_MinYaml_OmittedNetwork_MaxConcurrency_NoNormalizationErrorAndMatchesDefault()
    {
        var yaml = """
        rules:
          runner-label:
            severity: warning
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.Diagnostics.All(d =>
            !d.Message.Contains("max-concurrency", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Config!.Network.MaxConcurrency).IsEqualTo(LintConfigResourceLimits.DefaultNetworkMaxConcurrency);
    }

    [Test]
    public async Task Validate_Network_TimeoutSeconds_OverMaximum_ReturnsError_AndCaps()
    {
        var yaml = """
        network:
          timeout-seconds: 301
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(x =>
                x.Message.Contains("timeout-seconds must be <= ", StringComparison.Ordinal)))
            .IsTrue();
        await Assert.That(result.Config!.Network.TimeoutSeconds).IsEqualTo(LintConfigResourceLimits.MaxNetworkTimeoutSeconds);
    }

    [Test]
    public async Task Validate_Network_MaxConcurrency_OverMaximum_ReturnsError_AndCaps()
    {
        var yaml = """
        network:
          max-concurrency: 200
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsFalse();
        var cap = LintConfigResourceLimits.MaxNetworkConcurrencyCap;
        await Assert.That(result.Diagnostics.Any(x =>
                x.Message.Contains($"max-concurrency must be <= {cap}", StringComparison.Ordinal)))
            .IsTrue();
        await Assert.That(result.Config!.Network.MaxConcurrency).IsEqualTo(cap);
    }

    [Test]
    public async Task Validate_Utf8TextOverMaximum_ReturnsError()
    {
        var payload = new string('x', LintConfigResourceLimits.MaxConfigUtf8Bytes + 1);

        var result = LintConfigLibrary.Validate(payload, "big.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Config).IsNull();
        await Assert.That(result.Diagnostics.Single().Message.Contains("maximum size", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ValidateFile_OnDiskOverMaximum_ReturnsError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "seiton-p1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "seiton.yaml");
        try
        {
            var bytes = new byte[LintConfigResourceLimits.MaxConfigUtf8Bytes + 1];
            Array.Fill(bytes, (byte)' ');
            bytes[^1] = (byte)'\n';
            await File.WriteAllBytesAsync(path, bytes);

            var result = LintConfigLibrary.ValidateFile(path);

            await Assert.That(result.IsValid).IsFalse();
            await Assert.That(result.Config).IsNull();
            await Assert.That(result.Diagnostics.Single().Message.Contains("maximum size", StringComparison.Ordinal)).IsTrue();
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Test]
    public async Task Validate_YamlNesting_OverMaximumDepth_ReturnsError()
    {
        var yaml = BuildDeepMappingYaml(64);

        var result = LintConfigLibrary.Validate(yaml, "nested.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("maximum nesting depth", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Validate_YamlStructuralUnits_OverMaximum_ReturnsError()
    {
        var yaml = BuildLargeExtendListYaml(52_000);

        var result = LintConfigLibrary.Validate(yaml, "units.yaml");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("structural size", StringComparison.Ordinal))).IsTrue();
    }

    private static string BuildDeepMappingYaml(int descendantMappingCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("root:");
        var indent = new string(' ', 2);
        for (var i = 0; i < descendantMappingCount; i++)
        {
            sb.Append(indent).Append('k').Append(i).AppendLine(":");
            indent += "  ";
        }

        return sb.ToString();
    }

    private static string BuildLargeExtendListYaml(int itemCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("""
rules:
  credentials:
    public-registries:
      extend:
""");
        sb.Append(Environment.NewLine);
        var linePrefix = Environment.NewLine + "        - ";
        for (var i = 0; i < itemCount; i++)
        {
            sb.Append(linePrefix).Append('z');
        }

        sb.Append(Environment.NewLine);
        return sb.ToString();
    }

    [Test]
    public async Task GenerateTemplateYaml_AsIs_IsValidConfig()
    {
        // The template with all lines commented out should parse as a valid (empty) config
        var yaml = LintConfigLibrary.GenerateTemplateYaml();
        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task GenerateTemplateYaml_Uncommented_IsValidConfig()
    {
        // Uncomment only config-example lines (indented, not prose) and verify it parses without errors
        var yaml = LintConfigLibrary.GenerateTemplateYaml();
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var uncommented = string.Join('\n', lines.Select(line =>
        {
            var trimmed = line.TrimStart();
            // Only uncomment lines that are indented (inside a section) and whose
            // content after "# " looks like YAML config (starts with '- ' or 'word[-word]*:')
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                var indent = line.Length - trimmed.Length;
                if (indent == 0) return line; // header comments
                var content = trimmed[2..];
                if (content.Length > 0 && char.IsUpper(content[0])) return line; // English prose
                if (content.Contains("(omit", StringComparison.Ordinal)) return line; // documentation hint
                return new string(' ', indent) + content;
            }
            return line;
        }));

        var result = LintConfigLibrary.Validate(uncommented, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue()
            .Because($"Template uncommented should be valid, but got: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
    }

    [Test]
    public async Task GenerateTemplateYaml_UsesObjectOnlyIgnoreActionsExample()
    {
        var yaml = LintConfigLibrary.GenerateTemplateYaml();

        await Assert.That(yaml).Contains("owner: \"my-org/*\"");
        await Assert.That(yaml).DoesNotContain("- my-org/internal-action");
        await Assert.That(yaml).DoesNotContain("- my-org/setup-*");
    }
}
