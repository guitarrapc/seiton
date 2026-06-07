using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

/// <summary>
/// Regression tests for githubactions-lab migration config (see feedback_seiton.md).
/// Fixture: tests/Seiton.Core.Tests/fixtures/migration/
/// </summary>
public sealed class FeedbackMigrationRegressionTests
{
    private static string FixtureRoot =>
        Path.Combine(FindRepoRoot(), "tests", "Seiton.Core.Tests", "fixtures", "migration");

    private static string ConfigPath => Path.Combine(FixtureRoot, ".github", "seiton.yaml");

    private static string WorkflowsDir => Path.Combine(FixtureRoot, ".github", "workflows");

    [Test]
    public async Task ValidateFile_MigratedConfig_IsValid()
    {
        var result = LintConfigLibrary.ValidateFile(ConfigPath);
        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Config).IsNotNull();
    }

    [Test]
    public async Task ValidateExclusionJobIds_MigratedConfig_NoErrors()
    {
        var validation = LintConfigLibrary.ValidateFile(ConfigPath);
        var workflowPaths = ListWorkflowPaths();
        var diags = ExclusionJobIdValidator.Validate(
            validation.Config,
            workflowPaths,
            ConfigPath,
            out _);

        await Assert.That(diags.Any(d => d.Severity == DiagnosticSeverity.Error)).IsFalse();
    }

    [Test]
    public async Task Lint_AllDiscoveredWorkflows_NoUnknownJobIdConfigErrors()
    {
        var config = LoadConfig();
        foreach (var path in ListWorkflowPaths())
        {
            var bytes = File.ReadAllBytes(path);
            using var result = new LintEngine().Check(bytes, path, config);

            await Assert.That(result.Diagnostics.Any(d =>
                d.RuleId is null
                && d.Message.Contains("unknown job-id", StringComparison.Ordinal))).IsFalse();
        }
    }

    [Test]
    public async Task Lint_JobScopedExclusionForOtherFile_DoesNotInflateUnknownJobIdErrors()
    {
        var cleanPath = Path.Combine(WorkflowsDir, "clean-ci.yml");
        var baseConfig = LoadConfig();
        var config = CloneConfigWithExtraExclusion(
            baseConfig,
            new LintExclusion(
                ".github/workflows/reusable-workflow-caller-nest.yaml",
                ["deny-inherit-secrets"],
                Jobs: ["inherit-demo"]));

        var bytes = File.ReadAllBytes(cleanPath);
        using var result = new LintEngine().Check(bytes, cleanPath, config);

        await Assert.That(result.Diagnostics.Any(d =>
            d.Message.Contains("unknown job-id", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Lint_AgenticsMaintenance_FileExclusion_SuppressesAllWorkflowDiagnostics()
    {
        var path = Path.Combine(WorkflowsDir, "agentics-maintenance.yml");
        var bytes = File.ReadAllBytes(path);
        using var result = new LintEngine().Check(bytes, path, LoadConfig());

        await Assert.That(result.Diagnostics.Any(d => d.RuleId is not null)).IsFalse();
    }

    [Test]
    public async Task Lint_AgenticsMaintenance_RulesWildcard_MatchesFileOnlyExclusion()
    {
        var path = Path.Combine(WorkflowsDir, "agentics-maintenance.yml");
        var bytes = File.ReadAllBytes(path);
        var baseConfig = LoadConfig();
        var wildcardConfig = CloneConfigWithAgenticsWildcard(baseConfig);

        using var omitted = new LintEngine().Check(bytes, path, baseConfig);
        using var wildcard = new LintEngine().Check(bytes, path, wildcardConfig);

        await Assert.That(omitted.Diagnostics.Any(d => d.RuleId is not null)).IsFalse();
        await Assert.That(wildcard.Diagnostics.Any(d => d.RuleId is not null)).IsFalse();
    }

    [Test]
    public async Task Lint_MigratedExclusions_SuppressesExpectedRules()
    {
        var config = LoadConfig();

        await AssertSuppressedRule(
            Path.Combine(WorkflowsDir, "auto-dump-context.yaml"),
            config,
            "dangerous-triggers");

        await AssertSuppressedRule(
            Path.Combine(WorkflowsDir, "dump-context.yaml"),
            config,
            "dangerous-triggers");

        await AssertSuppressedRule(
            Path.Combine(WorkflowsDir, "reusable-workflow-caller-nest.yaml"),
            config,
            "deny-inherit-secrets");
    }

    [Test]
    public async Task Lint_MigratedExclusions_TotalSuppressed_CountsFeedbackRules()
    {
        var config = LoadConfig();
        var total = 0;

        foreach (var path in ListWorkflowPaths())
        {
            var bytes = File.ReadAllBytes(path);
            using var result = new LintEngine().Check(bytes, path, config);
            total += result.SuppressionSummary.TotalSuppressed;
        }

        await Assert.That(total).IsGreaterThanOrEqualTo(3);
        await Assert.That(total).IsLessThanOrEqualTo(5);
    }

    private static async Task AssertSuppressedRule(string path, LintConfig config, string ruleId)
    {
        var bytes = File.ReadAllBytes(path);
        using var result = new LintEngine().Check(bytes, path, config);

        await Assert.That(result.Diagnostics.Any(d => d.RuleId == ruleId)).IsFalse();
        await Assert.That(result.SuppressionSummary.SuppressedByRule.ContainsKey(ruleId)).IsTrue();
    }

    private static LintConfig LoadConfig()
    {
        var validation = LintConfigLibrary.ValidateFile(ConfigPath);
        if (!validation.IsValid || validation.Config is null)
        {
            throw new InvalidOperationException("Fixture config is invalid");
        }

        return validation.Config;
    }

    private static LintConfig CloneConfigWithExtraExclusion(LintConfig baseConfig, LintExclusion extra)
    {
        var exclusions = new List<LintExclusion>(baseConfig.Exclusions!.Count + 1);
        exclusions.AddRange(baseConfig.Exclusions);
        exclusions.Add(extra);

        return new LintConfig
        {
            Utf8Yaml = baseConfig.Utf8Yaml,
            FilePath = baseConfig.FilePath,
            ConfigFilePath = baseConfig.ConfigFilePath,
            Rules = baseConfig.Rules,
            Exclusions = exclusions,
            Fix = baseConfig.Fix,
            Network = baseConfig.Network,
            Discovery = baseConfig.Discovery,
        };
    }

    private static LintConfig CloneConfigWithAgenticsWildcard(LintConfig baseConfig)
    {
        var exclusions = new List<LintExclusion>(baseConfig.Exclusions!.Count);
        for (var i = 0; i < baseConfig.Exclusions.Count; i++)
        {
            var exclusion = baseConfig.Exclusions[i];
            if (exclusion.File.Contains("agentics-maintenance.yml", StringComparison.Ordinal))
            {
                exclusions.Add(new LintExclusion(exclusion.File, ["*"], exclusion.Jobs));
            }
            else
            {
                exclusions.Add(exclusion);
            }
        }

        return new LintConfig
        {
            Utf8Yaml = baseConfig.Utf8Yaml,
            FilePath = baseConfig.FilePath,
            ConfigFilePath = baseConfig.ConfigFilePath,
            Rules = baseConfig.Rules,
            Exclusions = exclusions,
            Fix = baseConfig.Fix,
            Network = baseConfig.Network,
            Discovery = baseConfig.Discovery,
        };
    }

    private static string[] ListWorkflowPaths()
    {
        return Directory
            .EnumerateFiles(WorkflowsDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(p => p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "seiton.slnx")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
