using System.Security;
using System.Text;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

/// <summary>
/// Entry point for loading, parsing, validating, and normalizing seiton configuration YAML.
/// </summary>
public static class LintConfigLibrary
{
    /// <summary>Gets the ordered list of recommended relative paths for the seiton configuration file.</summary>
    public static IReadOnlyList<string> RecommendedRelativePaths { get; } =
    [
        ".github/seiton.yaml",
        ".github/seiton.yml",
        "seiton.yaml",
        "seiton.yml",
    ];

    /// <summary>Generates a commented-out template YAML string for a new seiton configuration file.</summary>
    public static string GenerateTemplateYaml()
    {
        return """
        # Seiton linter configuration. see https://github.com/guitarrapc/seiton/blob/main/docs/configuration.md for details.
        # Preferred location: .github/seiton.yaml

        rules:
          # Add dangerous trigger events (appended to built-in set).
          # dangerous-triggers:
          #   severity: warning
          #   events:
          #     - issue_comment

          # Add known GitHub-hosted runner labels (appended to built-in set).
          # runner-label:
          #   known-hosted-labels:
          #     - ubuntu-24.04-large

          # Map moving labels to pinned replacements for detection/fix.
          # runner-no-latest:
          #   fix-mapping:
          #     ubuntu-latest: "ubuntu-24.04"
          #     windows-latest: "windows-2025"
          #     macos-latest: "macos-15"

          # Add public registries treated as credential-optional.
          # credentials:
          #   public-registries:
          #     - ghcr.io

          # Add untrusted triggers for cache poisoning checks.
          # cache-poisoning-trigger:
          #   untrusted-triggers:
          #     - issue_comment

          # Add untrusted triggers for self-hosted runner checks.
          # self-hosted-runner-trigger:
          #   untrusted-triggers:
          #     - issue_comment

          # Add output commands watched as secret sinks.
          # unredacted-secrets:
          #   output-commands:
          #     - tee

          # Define allow/deny patterns for uses references.
          # forbidden-uses:
          #   allow:
          #     - actions/*
          #   deny:
          #     - some-untrusted-org/*

          # Ignore selected actions from SHA pin checks.
          # unpinned-uses:
          #   ignore-actions:
          #     - owner: "my-org/*"
          #     - owner: "my-org/internal-action"
          #     - owner: "my-org/setup-*"
          #       refs: [main, master]

          # Tune secret count thresholds.
          # overprovisioned-secrets:
          #   max-step-env-secrets: 5
          #   max-job-secrets: 5

          # Assume additional events for expression validation.
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
          # Glob + jobs scope: exclude rules for specific jobs only
          # - file: .github/workflows/legacy-*.yml
          #   rules:
          #     - runner-no-latest
          #   jobs:
          #     - legacy
          # One file, multiple rules (entire file, no jobs scope):
          # - file: .github/workflows/demo.yml
          #   rules:
          #     - run-env-context-direct-use
          #     - unpinned-image
          # File-only exclusion (skips lint for the entire file):
          # - file: .github/workflows/generated.yml
          # Gh-aw file without # gh-aw-metadata: in the first 10 lines (e.g. agentics-maintenance.yml):
          # - file: .github/workflows/agentics-maintenance.yml

        discovery:
          # skip-agentic-workflows: true   # opt-in: skip workflows whose first 10 lines contain "# gh-aw-metadata:" (often *.lock.yml)

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
            #   - uses: "slsa-framework/*"
            #     ref: "*"
          images:
            # enable-network: false
            # exclude-images:
            #   - scratch
            # exclude-tags:
            #   - latest
            # ignore-images:
            #   - mcr.microsoft.com/**

        network:
          # on-error: skip
          # timeout-seconds: 30
          # max-concurrency: (omit; default is min(4, logical CPUs))
          # github:
          #   ghes-api-url: ""
          #   ghes-fallback: false

        output:
          # sort-order: location    # location (default) | rule

        """;
    }

    /// <summary>Searches <paramref name="repositoryRoot"/> for the first existing configuration file at a recommended path.</summary>
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

    /// <summary>Reads and validates the configuration file at <paramref name="configPath"/>.</summary>
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

        long fileLengthBytes;
        try
        {
            fileLengthBytes = new FileInfo(configPath).Length;
        }
        catch (Exception ex) when (IsConfigPathAccessFailure(ex))
        {
            return ConfigFileAccessDiagnostics(configPath, ex);
        }

        if (IsConfigFileOverSizeBytes(fileLengthBytes))
        {
            var tooLarge = new Diagnostic(
                DiagnosticSeverity.Error,
                $"seiton configuration file exceeds maximum size ({LintConfigResourceLimits.MaxConfigUtf8Bytes} bytes): '{configPath}'",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: configPath);
            return new LintConfigValidationResult(null, [tooLarge]);
        }

        string yamlText;
        try
        {
            yamlText = File.ReadAllText(configPath);
        }
        catch (Exception ex) when (IsConfigPathAccessFailure(ex))
        {
            return ConfigFileAccessDiagnostics(configPath, ex);
        }

        return Validate(yamlText, configPath);
    }

    /// <summary>True for failures when resolving or opening the config path (not parse/validation errors).</summary>
    private static bool IsConfigPathAccessFailure(Exception ex) =>
        ex is IOException
        or UnauthorizedAccessException
        or SecurityException;

    private static LintConfigValidationResult ConfigFileAccessDiagnostics(string configPath, Exception ex)
    {
        var diag = new Diagnostic(
            DiagnosticSeverity.Error,
            $"config file '{configPath}' could not be read: {ex.Message}",
            new TextRange(0, 1, 1, 1, 1, 2),
            FilePath: configPath);
        return new LintConfigValidationResult(null, [diag]);
    }

    private static bool IsConfigFileOverSizeBytes(long length) =>
        length > LintConfigResourceLimits.MaxConfigUtf8Bytes;

    /// <summary>Parses and validates the given YAML text as a seiton configuration.</summary>
    public static LintConfigValidationResult Validate(string yamlText, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlText);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        if (Encoding.UTF8.GetByteCount(yamlText) > LintConfigResourceLimits.MaxConfigUtf8Bytes)
        {
            var tooLarge = new Diagnostic(
                DiagnosticSeverity.Error,
                $"seiton configuration exceeds maximum size ({LintConfigResourceLimits.MaxConfigUtf8Bytes} UTF-8 bytes)",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath);
            return new LintConfigValidationResult(null, [tooLarge]);
        }

        var utf8Yaml = Encoding.UTF8.GetBytes(yamlText);
        var parseResult = LintConfigYamlParser.Parse(utf8Yaml.AsMemory(), filePath);

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
            Utf8Yaml = utf8Yaml,
            FilePath = filePath,
            ConfigFilePath = filePath,
            Rules = normalizedRules.Rules,
            Exclusions = normalizedExclusions.Exclusions,
            Fix = normalizedFix.Fix,
            Network = normalizedNetwork.Network,
            Output = parseResult.Output,
            Discovery = parseResult.Discovery,
        };

        return new LintConfigValidationResult(config, diagnostics.ToArray());
    }

    private static NormalizedRules NormalizeRules(Dictionary<string, RuleConfig>? rules, string filePath)
    {
        if (rules is null || rules.Count == 0)
        {
            return NormalizedRules.Empty;
        }

        var diagnostics = new List<Diagnostic>();
        var normalized = new Dictionary<string, RuleConfig>(StringComparer.Ordinal);
        RuleNormalizer.NormalizeRuleEntries(rules, filePath, diagnostics, normalized);
        return new NormalizedRules(normalized, diagnostics.ToArray());
    }

    private static NormalizedExclusions NormalizeExclusions(IReadOnlyList<LintExclusion>? exclusions, string filePath)
    {
        if (exclusions is null || exclusions.Count == 0)
        {
            return NormalizedExclusions.Empty;
        }

        var normalized = new List<LintExclusion>(exclusions.Count);
        var diagnostics = new List<Diagnostic>();
        var scopeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var scopeFilePatterns = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < exclusions.Count; i++)
        {
            var exclusion = exclusions[i];
            if (string.IsNullOrWhiteSpace(exclusion.File))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "exclusion file must not be empty",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            IReadOnlyList<string>? resolvedRules;
            if (exclusion.Rules is null)
            {
                // rules omitted → all rules (file/job-level exclusion)
                resolvedRules = null;
            }
            else if (exclusion.Rules.Count == 0)
            {
                // rules: [] → explicit empty, no-op
                continue;
            }
            else if (ExclusionNormalizer.IsAllRulesWildcard(exclusion.Rules))
            {
                // rules: ["*"] → all rules (same as omitting rules)
                resolvedRules = null;
            }
            else
            {
                var ruleIds = new HashSet<string>(StringComparer.Ordinal);
                ExclusionNormalizer.CollectResolvedExclusionRules(exclusion.Rules, filePath, diagnostics, ruleIds);

                if (ruleIds.Count == 0)
                {
                    continue;
                }

                resolvedRules = [.. ruleIds];
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

            var filePattern = exclusion.File.Trim();
            var normalizedFilePattern = ActionRefHelpers.NormalizePath(filePattern);
            var scopeKey = BuildExclusionScopeKey(normalizedFilePattern, jobs);
            scopeCounts.TryGetValue(scopeKey, out var seenCount);
            scopeCounts[scopeKey] = seenCount + 1;
            scopeFilePatterns.TryAdd(scopeKey, normalizedFilePattern);

            normalized.Add(new LintExclusion(filePattern, resolvedRules, jobs.Count > 0 ? jobs : null));
        }

        foreach (var (scopeKey, count) in scopeCounts)
        {
            if (count <= 1)
            {
                continue;
            }

            var filePattern = scopeFilePatterns[scopeKey];
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Info,
                $"exclusion for '{filePattern}' appears {count} times; consider merging rules into one entry",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath));
        }

        return new NormalizedExclusions(normalized, diagnostics.ToArray());
    }

    private static string BuildExclusionScopeKey(string filePattern, IReadOnlyList<string> jobs)
    {
        if (jobs.Count == 0)
        {
            return filePattern + "|";
        }

        var jobNames = new string[jobs.Count];
        for (var i = 0; i < jobs.Count; i++)
        {
            jobNames[i] = jobs[i];
        }

        Array.Sort(jobNames, StringComparer.Ordinal);
        return filePattern + "|" + string.Join(',', jobNames);
    }

    private static NormalizedFix NormalizeFix(FixConfig fix, string filePath)
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

    private static NormalizedNetwork NormalizeNetwork(NetworkConfig network, string filePath)
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
        else if (timeout > LintConfigResourceLimits.MaxNetworkTimeoutSeconds)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"network.timeout-seconds must be <= {LintConfigResourceLimits.MaxNetworkTimeoutSeconds}",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath));
            timeout = LintConfigResourceLimits.MaxNetworkTimeoutSeconds;
        }

        var maxConcurrency = network.MaxConcurrency;
        if (maxConcurrency <= 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "network.max-concurrency must be > 0",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath));
            maxConcurrency = LintConfigResourceLimits.DefaultNetworkMaxConcurrency;
        }
        else if (maxConcurrency > LintConfigResourceLimits.MaxNetworkConcurrencyCap)
        {
            var cap = LintConfigResourceLimits.MaxNetworkConcurrencyCap;
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"network.max-concurrency must be <= {cap} (logical processor count)",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath));
            maxConcurrency = cap;
        }

        var ghesApiUrl = network.GitHub.GhesApiUrl?.Trim();
        if (string.IsNullOrEmpty(ghesApiUrl))
        {
            ghesApiUrl = null;
        }
        else if (!GitHubEnterpriseApiBase.TryValidateForConfig(ghesApiUrl, out var canonicalGhes, out var ghesDiagnostic))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                ghesDiagnostic,
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath));
            ghesApiUrl = null;
        }
        else
        {
            ghesApiUrl = canonicalGhes;
        }

        var normalizedNetwork = network with
        {
            TimeoutSeconds = timeout,
            MaxConcurrency = maxConcurrency,
            GitHub = network.GitHub with { GhesApiUrl = ghesApiUrl },
        };

        return new NormalizedNetwork(normalizedNetwork, diagnostics.ToArray());
    }

    private static IReadOnlyList<string> NormalizeStringList(IReadOnlyList<string> values)
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

    private readonly record struct NormalizedRules(
        IReadOnlyDictionary<string, RuleConfig> Rules,
        Diagnostic[] Diagnostics)
    {
        public static NormalizedRules Empty { get; } = new(new Dictionary<string, RuleConfig>(StringComparer.Ordinal), []);
    }

    private readonly record struct NormalizedExclusions(
        IReadOnlyList<LintExclusion> Exclusions,
        Diagnostic[] Diagnostics)
    {
        public static NormalizedExclusions Empty { get; } = new([], []);
    }

    private readonly record struct NormalizedFix(
        FixConfig Fix,
        Diagnostic[] Diagnostics)
    {
        public static NormalizedFix Empty { get; } = new(new FixConfig(), []);
    }

    private readonly record struct NormalizedNetwork(
        NetworkConfig Network,
        Diagnostic[] Diagnostics)
    {
        public static NormalizedNetwork Empty { get; } = new(new NetworkConfig(), []);
    }
}
