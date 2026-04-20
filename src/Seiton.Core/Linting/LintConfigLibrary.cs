using System.Text;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

public static class LintConfigLibrary
{
    public static IReadOnlyList<string> RecommendedRelativePaths { get; } =
    [
        ".github/seiton.yaml",
        ".github/seiton.yml",
        "seiton.yaml",
        "seiton.yml",
    ];

    public static string GenerateTemplateYaml()
    {
        return """
        # Seiton linter configuration
        # Preferred location: .github/seiton.yaml

        rules:
          # Example: override a rule's behavior.
          # dangerous-triggers:
          #   severity: warning
          #   events:
          #     extend:
          #       - issue_comment

          # runner-label:
          #   known-hosted-labels:
          #     extend:
          #       - ubuntu-24.04-large

          # credentials:
          #   public-registries:
          #     extend:
          #       - ghcr.io

          # cache-poisoning:
          #   untrusted-triggers:
          #     extend:
          #       - issue_comment

          # unredacted-secrets:
          #   output-commands:
          #     extend:
          #       - tee

          # forbidden-uses:
          #   allow:
          #     - actions/*
          #   deny:
          #     - some-untrusted-org/*

          # overprovisioned-secrets:
          #   max-step-env-secrets: 5
          #   max-job-secrets: 5

          # expr-undefined-var:
          #   assume-events:
          #     - workflow_dispatch

          # Online rules (default: disabled). Enable individually:
          # known-vulnerable-actions:
          #   enabled: true
          # impostor-commit:
          #   enabled: true
          # ref-confusion:
          #   enabled: true
          # stale-action-refs:
          #   enabled: true

        exclusions:
          # - files: .github/workflows/legacy-*.yml
          #   rules:
          #     - runner-no-latest
          #   jobs:
          #     - legacy

        fix:
          defaults:
            # job-timeout-minutes: 15
          pinning:
            # enable-network: false
            # min-age-days: 14
            # exclude-branches:
            #   - main
            #   - master
            # ignore-actions:
            #   - uses: "slsa-framework/.*"
            #     ref: ".*"
          images:
            # exclude-images:
            #   - scratch
            # exclude-tags:
            #   - latest
            # ignore-images:
            #   - mcr.microsoft.com/**

        network:
          # on-error: skip
          # timeout-seconds: 30
          # max-concurrency: 4
          # github:
          #   ghes-api-url: ""
          #   ghes-fallback: false
        """;
    }

    public static string? FindRecommendedConfigPath(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositoryRoot);

        for (var i = 0; i < RecommendedRelativePaths.Count; i++)
        {
            var path = Path.Combine(repositoryRoot, RecommendedRelativePaths[i].Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public static LintConfigValidationResult ValidateFile(string configPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);

        if (!File.Exists(configPath))
        {
            var missing = new Diagnostic(
                DiagnosticSeverity.Error,
                $"config file '{configPath}' was not found",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: configPath);
            return new LintConfigValidationResult(null, [missing]);
        }

        var yamlText = File.ReadAllText(configPath);
        return Validate(yamlText, configPath);
    }

    public static LintConfigValidationResult Validate(string yamlText, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlText);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var parser = new LintConfigLineParser(yamlText, filePath);
        var parseResult = parser.Parse();

        var diagnostics = new List<Diagnostic>(parseResult.Diagnostics.Length + 16);
        diagnostics.AddRange(parseResult.Diagnostics);

        var normalizedRules = NormalizeRules(parseResult.Rules, filePath);
        diagnostics.AddRange(normalizedRules.Diagnostics);

        var normalizedExclusions = NormalizeExclusions(parseResult.Exclusions, filePath);
        diagnostics.AddRange(normalizedExclusions.Diagnostics);

        var normalizedFix = NormalizeFix(parseResult.Fix, filePath);
        diagnostics.AddRange(normalizedFix.Diagnostics);

        var normalizedNetwork = NormalizeNetwork(parseResult.Network, filePath);
        diagnostics.AddRange(normalizedNetwork.Diagnostics);

        var config = new LintConfig
        {
            Utf8Yaml = Encoding.UTF8.GetBytes(yamlText),
            FilePath = filePath,
            Rules = normalizedRules.Rules,
            Exclusions = normalizedExclusions.Exclusions,
            Fix = normalizedFix.Fix,
            Network = normalizedNetwork.Network,
        };

        return new LintConfigValidationResult(config, diagnostics.ToArray());
    }

    static NormalizedRules NormalizeRules(Dictionary<string, RuleConfig>? rules, string filePath)
    {
        if (rules is null || rules.Count == 0)
        {
            return NormalizedRules.Empty;
        }

        var diagnostics = new List<Diagnostic>();
        var normalized = new Dictionary<string, RuleConfig>(StringComparer.Ordinal);

        foreach (var pair in rules)
        {
            if (!RuleCatalog.TryResolveRuleId(pair.Key, out var resolvedRuleId))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    BuildUnknownRuleIdMessage(pair.Key),
                    new TextRange(0, pair.Key.Length, 1, 1, 1, 1 + pair.Key.Length),
                    FilePath: filePath));
                continue;
            }

            var config = pair.Value;
            if (!config.Enabled && RuleCatalog.IsNonDisableable(resolvedRuleId))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"rule '{resolvedRuleId}' is non-disableable",
                    new TextRange(0, pair.Key.Length, 1, 1, 1, 1 + pair.Key.Length),
                    FilePath: filePath));
                config = config with { Enabled = true };
            }

            if (config.Severity is not null
                && RuleCatalog.TryGetMinimumSeverity(resolvedRuleId, out var minimumSeverity)
                && config.Severity.Value < minimumSeverity)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"rule '{resolvedRuleId}' minimum severity is '{minimumSeverity}', but '{config.Severity.Value}' was specified",
                    new TextRange(0, pair.Key.Length, 1, 1, 1, 1 + pair.Key.Length),
                    FilePath: filePath));
                config = config with { Severity = null };
            }

            config = RuleSpecificConfigNormalizer.Normalize(config, resolvedRuleId, filePath, diagnostics);

            normalized[resolvedRuleId] = config;
        }

        return new NormalizedRules(normalized, diagnostics.ToArray());
    }

    static NormalizedExclusions NormalizeExclusions(IReadOnlyList<LintExclusion>? exclusions, string filePath)
    {
        if (exclusions is null || exclusions.Count == 0)
        {
            return NormalizedExclusions.Empty;
        }

        var normalized = new List<LintExclusion>(exclusions.Count);
        var diagnostics = new List<Diagnostic>();

        for (var i = 0; i < exclusions.Count; i++)
        {
            var exclusion = exclusions[i];
            if (string.IsNullOrWhiteSpace(exclusion.Files))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "exclusion files must not be empty",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            var ruleIds = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < exclusion.Rules.Count; j++)
            {
                var ruleId = exclusion.Rules[j];
                if (!RuleCatalog.TryResolveRuleId(ruleId, out var resolvedRuleId))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        BuildUnknownRuleIdMessage(ruleId),
                        new TextRange(0, ruleId.Length, 1, 1, 1, 1 + ruleId.Length),
                        FilePath: filePath));
                    continue;
                }

                if (RuleCatalog.IsNonDisableable(resolvedRuleId))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        $"rule '{resolvedRuleId}' is non-disableable",
                        new TextRange(0, ruleId.Length, 1, 1, 1, 1 + ruleId.Length),
                        FilePath: filePath));
                    continue;
                }

                ruleIds.Add(resolvedRuleId);
            }

            if (ruleIds.Count == 0)
            {
                continue;
            }

            IReadOnlyList<string> jobs = [];
            if (exclusion.Jobs is { Count: > 0 })
            {
                var normalizedJobs = new List<string>();
                for (var j = 0; j < exclusion.Jobs.Count; j++)
                {
                    var trimmed = exclusion.Jobs[j]?.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        normalizedJobs.Add(trimmed);
                    }
                }

                jobs = normalizedJobs;
            }

            normalized.Add(new LintExclusion(exclusion.Files.Trim(), [.. ruleIds], jobs.Count > 0 ? jobs : null));
        }

        return new NormalizedExclusions(normalized, diagnostics.ToArray());
    }

    static NormalizedFix NormalizeFix(FixConfig fix, string filePath)
    {
        var diagnostics = new List<Diagnostic>();
        var pinning = fix.Pinning;

        var normalizedIgnoreActions = new List<IgnoreActionEntry>(pinning.IgnoreActions.Count);
        for (var i = 0; i < pinning.IgnoreActions.Count; i++)
        {
            var entry = pinning.IgnoreActions[i];
            if (string.IsNullOrWhiteSpace(entry.NamePattern) || string.IsNullOrWhiteSpace(entry.RefPattern))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "fix.pinning.ignore-actions entries require non-empty uses and ref",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            normalizedIgnoreActions.Add(new IgnoreActionEntry(entry.NamePattern.Trim(), entry.RefPattern.Trim()));
        }

        var normalizedExcludeBranches = NormalizeStringList(pinning.ExcludeBranches);

        var normalizedPinning = pinning with
        {
            ExcludeBranches = normalizedExcludeBranches,
            IgnoreActions = normalizedIgnoreActions,
        };

        var normalizedFix = fix with
        {
            Pinning = normalizedPinning,
        };

        return new NormalizedFix(normalizedFix, diagnostics.ToArray());
    }

    static NormalizedNetwork NormalizeNetwork(NetworkConfig network, string filePath)
    {
        var diagnostics = new List<Diagnostic>();

        var timeout = network.TimeoutSeconds;
        if (timeout < 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "network.timeout-seconds must be >= 0",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath));
            timeout = 30;
        }

        var maxConcurrency = network.MaxConcurrency;
        if (maxConcurrency <= 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "network.max-concurrency must be > 0",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath));
            maxConcurrency = 4;
        }

        var ghesApiUrl = network.GitHub.GhesApiUrl?.Trim();
        if (string.IsNullOrEmpty(ghesApiUrl))
        {
            ghesApiUrl = null;
        }

        var normalizedNetwork = network with
        {
            TimeoutSeconds = timeout,
            MaxConcurrency = maxConcurrency,
            GitHub = network.GitHub with { GhesApiUrl = ghesApiUrl },
        };

        return new NormalizedNetwork(normalizedNetwork, diagnostics.ToArray());
    }

    static IReadOnlyList<string> NormalizeStringList(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < values.Count; i++)
        {
            var trimmed = values[i]?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }

    static string BuildUnknownRuleIdMessage(string input)
    {
        var suggestion = RuleCatalog.SuggestRuleId(input);
        if (suggestion is null)
        {
            return $"unknown rule-id '{input}'";
        }

        return $"unknown rule-id '{input}'. did you mean '{suggestion}'?";
    }

    readonly record struct NormalizedRules(
        IReadOnlyDictionary<string, RuleConfig> Rules,
        Diagnostic[] Diagnostics)
    {
        public static NormalizedRules Empty { get; } = new(new Dictionary<string, RuleConfig>(StringComparer.Ordinal), []);
    }

    readonly record struct NormalizedExclusions(
        IReadOnlyList<LintExclusion> Exclusions,
        Diagnostic[] Diagnostics)
    {
        public static NormalizedExclusions Empty { get; } = new([], []);
    }

    readonly record struct NormalizedFix(
        FixConfig Fix,
        Diagnostic[] Diagnostics)
    {
        public static NormalizedFix Empty { get; } = new(new FixConfig(), []);
    }

    readonly record struct NormalizedNetwork(
        NetworkConfig Network,
        Diagnostic[] Diagnostics)
    {
        public static NormalizedNetwork Empty { get; } = new(new NetworkConfig(), []);
    }
}
