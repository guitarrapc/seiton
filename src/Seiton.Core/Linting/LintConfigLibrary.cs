using System.Text;
using Seiton.Core.Linting.OnlineAudit;
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
          #   enabled: true
          #   severity: warning

        additiveCustomization:
          # Merged with built-in dangerous trigger events.
          additionalDangerousEvents:
            # - workflow_run

          # Merged with built-in hosted runner labels.
          additionalKnownHostedLabels:
            # - ubuntu-24.04-large

          # Merged with built-in public container registry hosts.
          additionalPublicRegistries:
            # - ghcr.io

        exclusions:
          # - filePattern: .github/workflows/legacy-*.yml
          #   ruleIds:
          #     - runner-label
          #   jobId: legacy

        exprContext:
          # Optional explicit event types for expression validation.
          eventTypes:
            # - workflow_dispatch

        # Optional default timeout used by job-timeout-minutes-required partial auto-fix.
        # <= 0 disables fix attachment for that rule.
        default_job_timeout_minutes_for_fix: 15

        pin_resolution:
          # Must be true to enable network-assisted pin remediation.
          allow_network: false
          github_actions:
            token_env_vars:
              # - SEITON_GITHUB_TOKEN
              # - GITHUB_TOKEN
            ghes_api_url: ""
            ghes_fallback: false
            ignore_actions:
              # - name: "slsa-framework/.*"
              #   ref: ".*"
            exclude_branches:
              # - main
              # - master
            min_age_days: 14
          images:
            exclude_images:
              # - scratch
            exclude_tags:
              # - latest
            ignore_images:
              # - mcr.microsoft.com/**
          fail_open: true
          request_timeout_sec: 30
          max_concurrency: 4

        online_audit:
          # Must be true to enable network-assisted advisory/ref audit.
          allow_network: false
          github_actions:
            token_env_vars:
              # - SEITON_GITHUB_TOKEN
              # - GITHUB_TOKEN
            ghes_api_url: ""
            ghes_fallback: false
            ignore_actions:
              # - name: "slsa-framework/.*"
              #   ref: ".*"
          fail_open: true
          request_timeout_sec: 30
          max_concurrency: 4
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

        var normalizedRuleOptions = NormalizeRuleOptions(parseResult.RuleOptions, filePath);
        diagnostics.AddRange(normalizedRuleOptions.Diagnostics);

        var normalizedAdditive = NormalizeAdditiveCustomization(parseResult.AdditiveCustomization, filePath);
        diagnostics.AddRange(normalizedAdditive.Diagnostics);

        var normalizedExclusions = NormalizeExclusions(parseResult.Exclusions, filePath);
        diagnostics.AddRange(normalizedExclusions.Diagnostics);

        var normalizedPinResolution = NormalizePinResolution(parseResult.PinResolution, filePath);
        diagnostics.AddRange(normalizedPinResolution.Diagnostics);

        var normalizedOnlineAudit = NormalizeOnlineAudit(parseResult.OnlineAudit, filePath);
        diagnostics.AddRange(normalizedOnlineAudit.Diagnostics);

        var config = new LintConfig
        {
            Utf8Yaml = Encoding.UTF8.GetBytes(yamlText),
            FilePath = filePath,
            RuleOptions = normalizedRuleOptions.RuleOptions,
            Exclusions = normalizedExclusions.Exclusions,
            ExprContext = parseResult.ExpressionContext,
            AdditiveCustomization = normalizedAdditive.AdditiveCustomization,
            DefaultJobTimeoutMinutesForFix = parseResult.DefaultJobTimeoutMinutesForFix,
            PinResolution = normalizedPinResolution.PinResolution,
            OnlineAudit = normalizedOnlineAudit.OnlineAudit,
        };

        return new LintConfigValidationResult(config, diagnostics.ToArray());
    }

    static NormalizedRuleOptions NormalizeRuleOptions(IReadOnlyDictionary<string, RuleOption>? ruleOptions, string filePath)
    {
        if (ruleOptions is null || ruleOptions.Count == 0)
        {
            return NormalizedRuleOptions.Empty;
        }

        var diagnostics = new List<Diagnostic>();
        var normalized = new Dictionary<string, RuleOption>(StringComparer.Ordinal);

        foreach (var pair in ruleOptions)
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

            var option = pair.Value;
            if (!option.Enabled && RuleCatalog.IsNonDisableable(resolvedRuleId))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"rule '{resolvedRuleId}' is non-disableable",
                    new TextRange(0, pair.Key.Length, 1, 1, 1, 1 + pair.Key.Length),
                    FilePath: filePath));
                option = option with { Enabled = true };
            }

            if (option.Severity is not null
                && RuleCatalog.TryGetMinimumSeverity(resolvedRuleId, out var minimumSeverity)
                && option.Severity.Value < minimumSeverity)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"rule '{resolvedRuleId}' minimum severity is '{minimumSeverity}', but '{option.Severity.Value}' was specified",
                    new TextRange(0, pair.Key.Length, 1, 1, 1, 1 + pair.Key.Length),
                    FilePath: filePath));
                option = option with { Severity = null };
            }

            normalized[resolvedRuleId] = option;
        }

        return new NormalizedRuleOptions(normalized, diagnostics.ToArray());
    }

    static NormalizedAdditiveCustomization NormalizeAdditiveCustomization(RuleSpecificAdditiveCustomization customization, string filePath)
    {
        var diagnostics = new List<Diagnostic>();
        var dangerousEvents = NormalizeAdditiveValues(
            customization.AdditionalDangerousEvents,
            "dangerous-triggers additional dangerous event must not be empty",
            filePath,
            diagnostics);
        var knownLabels = NormalizeAdditiveValues(
            customization.AdditionalKnownHostedLabels,
            "runner-label additional known hosted label must not be empty",
            filePath,
            diagnostics);
        var registries = NormalizeRegistryHosts(
            customization.AdditionalPublicRegistries,
            filePath,
            diagnostics);

        return new NormalizedAdditiveCustomization(
            new RuleSpecificAdditiveCustomization(dangerousEvents, knownLabels, registries),
            diagnostics.ToArray());
    }

    static IReadOnlyList<string>? NormalizeAdditiveValues(
        IReadOnlyList<string>? values,
        string emptyMessage,
        string filePath,
        List<Diagnostic> diagnostics)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var normalized = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < values.Count; i++)
        {
            var trimmed = values[i]?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    emptyMessage,
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            var normalizedValue = NormalizeAsciiLower(trimmed);
            if (seen.Add(normalizedValue))
            {
                normalized.Add(normalizedValue);
            }
        }

        return normalized.Count == 0 ? null : normalized;
    }

    static IReadOnlyList<string>? NormalizeRegistryHosts(
        IReadOnlyList<string>? values,
        string filePath,
        List<Diagnostic> diagnostics)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var normalized = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < values.Count; i++)
        {
            var trimmed = values[i]?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "credentials additional public registry host must not be empty",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            if (!IsValidRegistryHost(trimmed))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"credentials additional public registry host '{trimmed}' is invalid",
                    new TextRange(0, trimmed.Length, 1, 1, 1, 1 + trimmed.Length),
                    FilePath: filePath));
                continue;
            }

            var normalizedValue = NormalizeAsciiLower(trimmed);
            if (seen.Add(normalizedValue))
            {
                normalized.Add(normalizedValue);
            }
        }

        return normalized.Count == 0 ? null : normalized;
    }

    static bool IsValidRegistryHost(string value)
    {
        if (value.Contains("://", StringComparison.Ordinal)
            || value.Contains('/')
            || value.Contains('\\'))
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
            {
                return false;
            }
        }

        var colonIndex = value.IndexOf(':');
        if (colonIndex < 0)
        {
            return value.Length > 0;
        }

        if (value.LastIndexOf(':') != colonIndex || colonIndex == 0 || colonIndex == value.Length - 1)
        {
            return false;
        }

        for (var i = colonIndex + 1; i < value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    static string NormalizeAsciiLower(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var buffer = value.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            var ch = buffer[i];
            if (ch is >= 'A' and <= 'Z')
            {
                buffer[i] = (char)(ch + 32);
            }
        }

        return new string(buffer);
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
            if (string.IsNullOrWhiteSpace(exclusion.FilePattern))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "exclusion file pattern must not be empty",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            var ruleIds = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < exclusion.RuleIds.Count; j++)
            {
                var ruleId = exclusion.RuleIds[j];
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

            normalized.Add(new LintExclusion(exclusion.FilePattern.Trim(), [.. ruleIds], exclusion.JobId?.Trim()));
        }

        return new NormalizedExclusions(normalized, diagnostics.ToArray());
    }

    static NormalizedPinResolution NormalizePinResolution(PinResolutionConfig? pinResolution, string filePath)
    {
        if (pinResolution is null)
        {
            return NormalizedPinResolution.Empty;
        }

        var diagnostics = new List<Diagnostic>();
        var timeout = pinResolution.RequestTimeoutSec;
        if (timeout < 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "pin_resolution.request_timeout_sec must be >= 0",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath));
            timeout = 30;
        }

        var maxConcurrency = pinResolution.MaxConcurrency;
        if (maxConcurrency <= 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "pin_resolution.max_concurrency must be > 0",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath));
            maxConcurrency = 4;
        }

        var normalizedTokenEnvVars = NormalizeStringList(pinResolution.GitHubActions.TokenEnvVars);
        var normalizedExcludeBranches = NormalizeStringList(pinResolution.GitHubActions.ExcludeBranches);
        var normalizedExcludeImages = NormalizeStringList(pinResolution.Images.ExcludeImages);
        var normalizedExcludeTags = NormalizeStringList(pinResolution.Images.ExcludeTags);
        var normalizedIgnoreImages = NormalizeStringList(pinResolution.Images.IgnoreImages);

        var normalizedIgnoreActions = new List<IgnoreActionEntry>(pinResolution.GitHubActions.IgnoreActions.Count);
        for (var i = 0; i < pinResolution.GitHubActions.IgnoreActions.Count; i++)
        {
            var entry = pinResolution.GitHubActions.IgnoreActions[i];
            if (string.IsNullOrWhiteSpace(entry.NamePattern) || string.IsNullOrWhiteSpace(entry.RefPattern))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "pin_resolution.github_actions.ignore_actions entries require non-empty name and ref",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            normalizedIgnoreActions.Add(new IgnoreActionEntry(entry.NamePattern.Trim(), entry.RefPattern.Trim()));
        }

        var ghesApiUrl = pinResolution.GitHubActions.GhesApiUrl?.Trim();
        if (string.IsNullOrEmpty(ghesApiUrl))
        {
            ghesApiUrl = null;
        }

        var normalized = new PinResolutionConfig
        {
            AllowNetwork = pinResolution.AllowNetwork,
            GitHubActions = new GitHubActionsResolutionConfig
            {
                TokenEnvVars = normalizedTokenEnvVars,
                GhesApiUrl = ghesApiUrl,
                GhesFallback = pinResolution.GitHubActions.GhesFallback,
                IgnoreActions = normalizedIgnoreActions,
                ExcludeBranches = normalizedExcludeBranches,
                MinAgeDays = pinResolution.GitHubActions.MinAgeDays,
            },
            Images = new ImageResolutionConfig
            {
                ExcludeImages = normalizedExcludeImages,
                ExcludeTags = normalizedExcludeTags,
                IgnoreImages = normalizedIgnoreImages,
            },
            FailOpen = pinResolution.FailOpen,
            RequestTimeoutSec = timeout,
            MaxConcurrency = maxConcurrency,
        };

        return new NormalizedPinResolution(normalized, diagnostics.ToArray());
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

    readonly record struct NormalizedRuleOptions(
        IReadOnlyDictionary<string, RuleOption> RuleOptions,
        Diagnostic[] Diagnostics)
    {
        public static NormalizedRuleOptions Empty { get; } = new(new Dictionary<string, RuleOption>(StringComparer.Ordinal), []);
    }

    readonly record struct NormalizedAdditiveCustomization(
        RuleSpecificAdditiveCustomization AdditiveCustomization,
        Diagnostic[] Diagnostics);

    readonly record struct NormalizedExclusions(
        IReadOnlyList<LintExclusion> Exclusions,
        Diagnostic[] Diagnostics)
    {
        public static NormalizedExclusions Empty { get; } = new([], []);
    }

    readonly record struct NormalizedPinResolution(
        PinResolutionConfig? PinResolution,
        Diagnostic[] Diagnostics)
    {
        public static NormalizedPinResolution Empty { get; } = new(null, []);
    }

    static NormalizedOnlineAudit NormalizeOnlineAudit(OnlineAuditConfig? onlineAudit, string filePath)
    {
        if (onlineAudit is null)
        {
            return NormalizedOnlineAudit.Empty;
        }

        var diagnostics = new List<Diagnostic>();
        var timeout = onlineAudit.RequestTimeoutSec;
        if (timeout < 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "online_audit.request_timeout_sec must be >= 0",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath));
            timeout = 30;
        }

        var maxConcurrency = onlineAudit.MaxConcurrency;
        if (maxConcurrency <= 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "online_audit.max_concurrency must be > 0",
                new TextRange(0, 1, 1, 1, 1, 2),
                FilePath: filePath));
            maxConcurrency = 4;
        }

        var normalizedTokenEnvVars = NormalizeStringList(onlineAudit.GitHubActions.TokenEnvVars);
        var normalizedIgnoreActions = new List<IgnoreActionEntry>(onlineAudit.GitHubActions.IgnoreActions.Count);
        for (var i = 0; i < onlineAudit.GitHubActions.IgnoreActions.Count; i++)
        {
            var entry = onlineAudit.GitHubActions.IgnoreActions[i];
            if (string.IsNullOrWhiteSpace(entry.NamePattern) || string.IsNullOrWhiteSpace(entry.RefPattern))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "online_audit.github_actions.ignore_actions entries require non-empty name and ref",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            normalizedIgnoreActions.Add(new IgnoreActionEntry(entry.NamePattern.Trim(), entry.RefPattern.Trim()));
        }

        var ghesApiUrl = onlineAudit.GitHubActions.GhesApiUrl?.Trim();
        if (string.IsNullOrEmpty(ghesApiUrl))
        {
            ghesApiUrl = null;
        }

        var normalized = new OnlineAuditConfig
        {
            AllowNetwork = onlineAudit.AllowNetwork,
            GitHubActions = new OnlineAuditGitHubConfig
            {
                TokenEnvVars = normalizedTokenEnvVars,
                GhesApiUrl = ghesApiUrl,
                GhesFallback = onlineAudit.GitHubActions.GhesFallback,
                IgnoreActions = normalizedIgnoreActions,
            },
            FailOpen = onlineAudit.FailOpen,
            RequestTimeoutSec = timeout,
            MaxConcurrency = maxConcurrency,
        };

        return new NormalizedOnlineAudit(normalized, diagnostics.ToArray());
    }

    readonly record struct NormalizedOnlineAudit(
        OnlineAuditConfig? OnlineAudit,
        Diagnostic[] Diagnostics)
    {
        public static NormalizedOnlineAudit Empty { get; } = new(null, []);
    }
}

public readonly record struct LintConfigValidationResult(
    LintConfig? Config,
    Diagnostic[] Diagnostics)
{
    public bool IsValid
    {
        get
        {
            for (var i = 0; i < Diagnostics.Length; i++)
            {
                if (Diagnostics[i].Severity == DiagnosticSeverity.Error)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

internal sealed class LintConfigLineParser
{
    readonly string[] lines;
    readonly string filePath;

    int index;

    readonly Dictionary<string, RuleOption> ruleOptions = new(StringComparer.OrdinalIgnoreCase);
    readonly List<LintExclusion> exclusions = [];
    readonly List<Diagnostic> diagnostics = [];
    RuleSpecificAdditiveCustomization additiveCustomization = RuleSpecificAdditiveCustomization.Empty;
    ExpressionContext expressionContext = ExpressionContext.Empty;
    int? defaultJobTimeoutMinutesForFix;
    PinResolutionConfig? pinResolution;
    OnlineAuditConfig? onlineAudit;

    public LintConfigLineParser(string yamlText, string filePath)
    {
        ArgumentNullException.ThrowIfNull(yamlText);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        lines = yamlText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        this.filePath = filePath;
    }

    public ParseResult Parse()
    {
        while (index < lines.Length)
        {
            var lineNumber = index + 1;
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent != 0)
            {
                diagnostics.Add(CreateError("expected top-level mapping key", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                diagnostics.Add(CreateError("expected mapping key", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            index++;
            if (key is "default_job_timeout_minutes_for_fix" or "defaultJobTimeoutMinutesForFix")
            {
                if (!TryParseInt(value, out var parsedDefaultTimeout))
                {
                    diagnostics.Add(CreateError("default_job_timeout_minutes_for_fix must be an integer", lineNumber, 1, line.Trim().Length));
                }
                else
                {
                    defaultJobTimeoutMinutesForFix = parsedDefaultTimeout;
                }

                continue;
            }

            if (key == "rules")
            {
                if (!string.IsNullOrEmpty(value))
                {
                    diagnostics.Add(CreateError("rules must be a mapping section", lineNumber, 1, line.Trim().Length));
                    continue;
                }

                ParseRulesSection();
                continue;
            }

            if (key == "additiveCustomization")
            {
                if (!string.IsNullOrEmpty(value))
                {
                    diagnostics.Add(CreateError("additiveCustomization must be a mapping section", lineNumber, 1, line.Trim().Length));
                    continue;
                }

                ParseAdditiveSection();
                continue;
            }

            if (key == "exclusions")
            {
                if (!string.IsNullOrEmpty(value))
                {
                    diagnostics.Add(CreateError("exclusions must be a sequence section", lineNumber, 1, line.Trim().Length));
                    continue;
                }

                ParseExclusionsSection();
                continue;
            }

            if (key == "exprContext")
            {
                if (!string.IsNullOrEmpty(value))
                {
                    diagnostics.Add(CreateError("exprContext must be a mapping section", lineNumber, 1, line.Trim().Length));
                    continue;
                }

                ParseExpressionContextSection();
                continue;
            }

            if (key is "pin_resolution" or "pinResolution")
            {
                if (!string.IsNullOrEmpty(value))
                {
                    diagnostics.Add(CreateError("pin_resolution must be a mapping section", lineNumber, 1, line.Trim().Length));
                    continue;
                }

                ParsePinResolutionSection();
                continue;
            }

            if (key is "online_audit" or "onlineAudit")
            {
                if (!string.IsNullOrEmpty(value))
                {
                    diagnostics.Add(CreateError("online_audit must be a mapping section", lineNumber, 1, line.Trim().Length));
                    continue;
                }

                ParseOnlineAuditSection();
                continue;
            }

            diagnostics.Add(CreateError($"unknown top-level key '{key}'", lineNumber, 1, key.Length));
            if (string.IsNullOrEmpty(value))
            {
                SkipIndentedBlock(0);
            }
        }

        return new ParseResult(
            new Dictionary<string, RuleOption>(ruleOptions, StringComparer.Ordinal),
            additiveCustomization,
            exclusions.ToArray(),
            expressionContext,
            defaultJobTimeoutMinutesForFix,
            pinResolution,
            onlineAudit,
            diagnostics.ToArray());
    }

    void ParseOnlineAuditSection()
    {
        var allowNetwork = false;
        var failOpen = true;
        var requestTimeoutSec = 30;
        var maxConcurrency = 4;
        var githubActions = new OnlineAuditGitHubConfig();

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 0)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 2)
            {
                diagnostics.Add(CreateError("online_audit key must be indented by 2 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                if (!TryParseKey(line, out key))
                {
                    diagnostics.Add(CreateError("online_audit entry must be key or key: value", lineNumber, 3, line.Trim().Length));
                    index++;
                    continue;
                }

                index++;
                if (key is "github_actions" or "githubActions")
                {
                    githubActions = ParseOnlineAuditGitHubActionsSection();
                    continue;
                }

                diagnostics.Add(CreateError($"unknown online_audit key '{key}'", lineNumber, 3, key.Length));
                SkipIndentedBlock(2);
                continue;
            }

            if (string.IsNullOrEmpty(value) && key is "github_actions" or "githubActions")
            {
                index++;
                githubActions = ParseOnlineAuditGitHubActionsSection();
                continue;
            }

            if (key is "allow_network" or "allowNetwork")
            {
                if (!TryParseBool(value, out var parsed))
                {
                    diagnostics.Add(CreateError("online_audit.allow_network must be true or false", lineNumber, 3, line.Trim().Length));
                }
                else
                {
                    allowNetwork = parsed;
                }

                index++;
                continue;
            }

            if (key is "fail_open" or "failOpen")
            {
                if (!TryParseBool(value, out var parsed))
                {
                    diagnostics.Add(CreateError("online_audit.fail_open must be true or false", lineNumber, 3, line.Trim().Length));
                }
                else
                {
                    failOpen = parsed;
                }

                index++;
                continue;
            }

            if (key is "request_timeout_sec" or "requestTimeoutSec")
            {
                if (!TryParseInt(value, out var parsed))
                {
                    diagnostics.Add(CreateError("online_audit.request_timeout_sec must be an integer", lineNumber, 3, line.Trim().Length));
                }
                else
                {
                    requestTimeoutSec = parsed;
                }

                index++;
                continue;
            }

            if (key is "max_concurrency" or "maxConcurrency")
            {
                if (!TryParseInt(value, out var parsed))
                {
                    diagnostics.Add(CreateError("online_audit.max_concurrency must be an integer", lineNumber, 3, line.Trim().Length));
                }
                else
                {
                    maxConcurrency = parsed;
                }

                index++;
                continue;
            }

            diagnostics.Add(CreateError($"unknown online_audit key '{key}'", lineNumber, 3, key.Length));
            index++;
        }

        onlineAudit = new OnlineAuditConfig
        {
            AllowNetwork = allowNetwork,
            GitHubActions = githubActions,
            FailOpen = failOpen,
            RequestTimeoutSec = requestTimeoutSec,
            MaxConcurrency = maxConcurrency,
        };
    }

    OnlineAuditGitHubConfig ParseOnlineAuditGitHubActionsSection()
    {
        IReadOnlyList<string>? tokenEnvVars = null;
        string? ghesApiUrl = null;
        var ghesFallback = false;
        IReadOnlyList<IgnoreActionEntry>? ignoreActions = null;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 2)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 4)
            {
                diagnostics.Add(CreateError("online_audit.github_actions key must be indented by 4 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                if (!TryParseKey(line, out key))
                {
                    diagnostics.Add(CreateError("online_audit.github_actions entry must be key or key: value", lineNumber, 5, line.Trim().Length));
                    index++;
                    continue;
                }

                index++;
                if (key is "token_env_vars" or "tokenEnvVars")
                {
                    tokenEnvVars = ParseListBlock(4, "token_env_vars");
                    continue;
                }

                if (key is "ignore_actions" or "ignoreActions")
                {
                    ignoreActions = ParseIgnoreActionsList(4);
                    continue;
                }

                diagnostics.Add(CreateError($"unknown online_audit.github_actions key '{key}'", lineNumber, 5, key.Length));
                SkipIndentedBlock(4);
                continue;
            }

            if (string.IsNullOrEmpty(value) && key is "token_env_vars" or "tokenEnvVars")
            {
                index++;
                tokenEnvVars = ParseListBlock(4, "token_env_vars");
                continue;
            }

            if (string.IsNullOrEmpty(value) && key is "ignore_actions" or "ignoreActions")
            {
                index++;
                ignoreActions = ParseIgnoreActionsList(4);
                continue;
            }

            if (key is "ghes_api_url" or "ghesApiUrl")
            {
                ghesApiUrl = Unquote(value);
                index++;
                continue;
            }

            if (key is "ghes_fallback" or "ghesFallback")
            {
                if (!TryParseBool(value, out var parsed))
                {
                    diagnostics.Add(CreateError("online_audit.github_actions.ghes_fallback must be true or false", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    ghesFallback = parsed;
                }

                index++;
                continue;
            }

            diagnostics.Add(CreateError($"unknown online_audit.github_actions key '{key}'", lineNumber, 5, key.Length));
            index++;
        }

        return new OnlineAuditGitHubConfig
        {
            TokenEnvVars = tokenEnvVars ?? new OnlineAuditGitHubConfig().TokenEnvVars,
            GhesApiUrl = ghesApiUrl,
            GhesFallback = ghesFallback,
            IgnoreActions = ignoreActions ?? [],
        };
    }

    void ParsePinResolutionSection()
    {
        var allowNetwork = false;
        var failOpen = true;
        var requestTimeoutSec = 30;
        var maxConcurrency = 4;
        var githubActions = new GitHubActionsResolutionConfig();
        var images = new ImageResolutionConfig();

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 0)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 2)
            {
                diagnostics.Add(CreateError("pin_resolution key must be indented by 2 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                if (!TryParseKey(line, out key))
                {
                    diagnostics.Add(CreateError("pin_resolution entry must be key or key: value", lineNumber, 3, line.Trim().Length));
                    index++;
                    continue;
                }

                index++;
                if (key is "github_actions" or "githubActions")
                {
                    githubActions = ParseGitHubActionsSection();
                    continue;
                }

                if (key == "images")
                {
                    images = ParseImagesSection();
                    continue;
                }

                diagnostics.Add(CreateError($"unknown pin_resolution key '{key}'", lineNumber, 3, key.Length));
                SkipIndentedBlock(2);
                continue;
            }

            if (string.IsNullOrEmpty(value) && key is "github_actions" or "githubActions")
            {
                index++;
                githubActions = ParseGitHubActionsSection();
                continue;
            }

            if (string.IsNullOrEmpty(value) && key == "images")
            {
                index++;
                images = ParseImagesSection();
                continue;
            }

            if (key is "allow_network" or "allowNetwork")
            {
                if (!TryParseBool(value, out var parsed))
                {
                    diagnostics.Add(CreateError("pin_resolution.allow_network must be true or false", lineNumber, 3, line.Trim().Length));
                }
                else
                {
                    allowNetwork = parsed;
                }

                index++;
                continue;
            }

            if (key is "fail_open" or "failOpen")
            {
                if (!TryParseBool(value, out var parsed))
                {
                    diagnostics.Add(CreateError("pin_resolution.fail_open must be true or false", lineNumber, 3, line.Trim().Length));
                }
                else
                {
                    failOpen = parsed;
                }

                index++;
                continue;
            }

            if (key is "request_timeout_sec" or "requestTimeoutSec")
            {
                if (!TryParseInt(value, out var parsed))
                {
                    diagnostics.Add(CreateError("pin_resolution.request_timeout_sec must be an integer", lineNumber, 3, line.Trim().Length));
                }
                else
                {
                    requestTimeoutSec = parsed;
                }

                index++;
                continue;
            }

            if (key is "max_concurrency" or "maxConcurrency")
            {
                if (!TryParseInt(value, out var parsed))
                {
                    diagnostics.Add(CreateError("pin_resolution.max_concurrency must be an integer", lineNumber, 3, line.Trim().Length));
                }
                else
                {
                    maxConcurrency = parsed;
                }

                index++;
                continue;
            }

            diagnostics.Add(CreateError($"unknown pin_resolution key '{key}'", lineNumber, 3, key.Length));
            index++;
        }

        pinResolution = new PinResolutionConfig
        {
            AllowNetwork = allowNetwork,
            GitHubActions = githubActions,
            Images = images,
            FailOpen = failOpen,
            RequestTimeoutSec = requestTimeoutSec,
            MaxConcurrency = maxConcurrency,
        };
    }

    GitHubActionsResolutionConfig ParseGitHubActionsSection()
    {
        IReadOnlyList<string>? tokenEnvVars = null;
        string? ghesApiUrl = null;
        var ghesFallback = false;
        IReadOnlyList<IgnoreActionEntry>? ignoreActions = null;
        IReadOnlyList<string>? excludeBranches = null;
        var minAgeDays = new GitHubActionsResolutionConfig().MinAgeDays;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 2)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 4)
            {
                diagnostics.Add(CreateError("pin_resolution.github_actions key must be indented by 4 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                if (!TryParseKey(line, out key))
                {
                    diagnostics.Add(CreateError("pin_resolution.github_actions entry must be key or key: value", lineNumber, 5, line.Trim().Length));
                    index++;
                    continue;
                }

                index++;
                if (key is "token_env_vars" or "tokenEnvVars")
                {
                    tokenEnvVars = ParseListBlock(4, "token_env_vars");
                    continue;
                }

                if (key is "ignore_actions" or "ignoreActions")
                {
                    ignoreActions = ParseIgnoreActionsList(4);
                    continue;
                }

                if (key is "exclude_branches" or "excludeBranches")
                {
                    excludeBranches = ParseListBlock(4, "exclude_branches");
                    continue;
                }

                diagnostics.Add(CreateError($"unknown pin_resolution.github_actions key '{key}'", lineNumber, 5, key.Length));
                SkipIndentedBlock(4);
                continue;
            }

            if (string.IsNullOrEmpty(value) && key is "token_env_vars" or "tokenEnvVars")
            {
                index++;
                tokenEnvVars = ParseListBlock(4, "token_env_vars");
                continue;
            }

            if (string.IsNullOrEmpty(value) && key is "ignore_actions" or "ignoreActions")
            {
                index++;
                ignoreActions = ParseIgnoreActionsList(4);
                continue;
            }

            if (string.IsNullOrEmpty(value) && key is "exclude_branches" or "excludeBranches")
            {
                index++;
                excludeBranches = ParseListBlock(4, "exclude_branches");
                continue;
            }

            if (key is "ghes_api_url" or "ghesApiUrl")
            {
                ghesApiUrl = Unquote(value);
                index++;
                continue;
            }

            if (key is "ghes_fallback" or "ghesFallback")
            {
                if (!TryParseBool(value, out var parsed))
                {
                    diagnostics.Add(CreateError("pin_resolution.github_actions.ghes_fallback must be true or false", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    ghesFallback = parsed;
                }

                index++;
                continue;
            }

            if (key is "min_age_days" or "minAgeDays")
            {
                if (!TryParseInt(value, out var parsed))
                {
                    diagnostics.Add(CreateError("pin_resolution.github_actions.min_age_days must be an integer", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    minAgeDays = parsed;
                }

                index++;
                continue;
            }

            diagnostics.Add(CreateError($"unknown pin_resolution.github_actions key '{key}'", lineNumber, 5, key.Length));
            index++;
        }

        return new GitHubActionsResolutionConfig
        {
            TokenEnvVars = tokenEnvVars ?? new GitHubActionsResolutionConfig().TokenEnvVars,
            GhesApiUrl = ghesApiUrl,
            GhesFallback = ghesFallback,
            IgnoreActions = ignoreActions ?? [],
            ExcludeBranches = excludeBranches ?? new GitHubActionsResolutionConfig().ExcludeBranches,
            MinAgeDays = minAgeDays,
        };
    }

    ImageResolutionConfig ParseImagesSection()
    {
        IReadOnlyList<string>? excludeImages = null;
        IReadOnlyList<string>? excludeTags = null;
        IReadOnlyList<string>? ignoreImages = null;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 2)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 4)
            {
                diagnostics.Add(CreateError("pin_resolution.images key must be indented by 4 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseKey(line, out var key))
            {
                diagnostics.Add(CreateError("pin_resolution.images entry must be a key", lineNumber, 5, line.Trim().Length));
                index++;
                continue;
            }

            index++;
            if (key is "exclude_images" or "excludeImages")
            {
                excludeImages = ParseListBlock(4, "exclude_images");
                continue;
            }

            if (key is "exclude_tags" or "excludeTags")
            {
                excludeTags = ParseListBlock(4, "exclude_tags");
                continue;
            }

            if (key is "ignore_images" or "ignoreImages")
            {
                ignoreImages = ParseListBlock(4, "ignore_images");
                continue;
            }

            diagnostics.Add(CreateError($"unknown pin_resolution.images key '{key}'", lineNumber, 5, key.Length));
            SkipIndentedBlock(4);
        }

        return new ImageResolutionConfig
        {
            ExcludeImages = excludeImages ?? new ImageResolutionConfig().ExcludeImages,
            ExcludeTags = excludeTags ?? new ImageResolutionConfig().ExcludeTags,
            IgnoreImages = ignoreImages ?? [],
        };
    }

    IReadOnlyList<IgnoreActionEntry> ParseIgnoreActionsList(int parentIndent)
    {
        var result = new List<IgnoreActionEntry>();

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= parentIndent)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != parentIndent + 2)
            {
                diagnostics.Add(CreateError("ignore_actions list entry must be indented by 6 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                diagnostics.Add(CreateError("ignore_actions must be a YAML list", lineNumber, parentIndent + 3, trimmed.Length));
                index++;
                continue;
            }

            string? name = null;
            string? reference = null;
            var inline = trimmed[1..].Trim();
            if (inline.Length > 0)
            {
                if (!TryParseProperty(inline, out var inlineKey, out var inlineValue))
                {
                    diagnostics.Add(CreateError("ignore_actions item must be 'name: ...' or 'ref: ...'", lineNumber, parentIndent + 3, trimmed.Length));
                }
                else if (inlineKey == "name")
                {
                    name = Unquote(inlineValue);
                }
                else if (inlineKey == "ref")
                {
                    reference = Unquote(inlineValue);
                }
                else
                {
                    diagnostics.Add(CreateError($"unknown ignore_actions inline key '{inlineKey}'", lineNumber, parentIndent + 3, inlineKey.Length));
                }
            }

            index++;
            while (index < lines.Length)
            {
                var subLine = lines[index];
                if (TrySkip(subLine))
                {
                    index++;
                    continue;
                }

                var subIndent = GetIndent(subLine);
                if (subIndent <= parentIndent + 2)
                {
                    break;
                }

                if (subIndent != parentIndent + 4)
                {
                    diagnostics.Add(CreateError("ignore_actions fields must be indented by 8 spaces", index + 1, subIndent + 1, subLine.Trim().Length));
                    index++;
                    continue;
                }

                if (!TryParseProperty(subLine, out var key, out var value))
                {
                    diagnostics.Add(CreateError("ignore_actions field must be key: value", index + 1, parentIndent + 5, subLine.Trim().Length));
                    index++;
                    continue;
                }

                if (key == "name")
                {
                    name = Unquote(value);
                    index++;
                    continue;
                }

                if (key == "ref")
                {
                    reference = Unquote(value);
                    index++;
                    continue;
                }

                diagnostics.Add(CreateError($"unknown ignore_actions key '{key}'", index + 1, parentIndent + 5, key.Length));
                index++;
            }

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(reference))
            {
                diagnostics.Add(CreateError("ignore_actions requires both name and ref", lineNumber, parentIndent + 3, trimmed.Length));
                continue;
            }

            result.Add(new IgnoreActionEntry(name, reference));
        }

        return result;
    }

    void ParseRulesSection()
    {
        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 0)
            {
                return;
            }

            var lineNumber = index + 1;
            if (indent != 2)
            {
                diagnostics.Add(CreateError("rules entry must be indented by 2 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseKey(line, out var ruleId))
            {
                diagnostics.Add(CreateError("rules entry must be a mapping key", lineNumber, 3, line.Trim().Length));
                index++;
                continue;
            }

            index++;
            ParseRuleBody(ruleId, lineNumber);
        }
    }

    void ParseRuleBody(string ruleId, int ruleLineNumber)
    {
        var enabled = true;
        DiagnosticSeverity? severity = null;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 2)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 4)
            {
                diagnostics.Add(CreateError("rule option must be indented by 4 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                diagnostics.Add(CreateError("rule option must be key: value", lineNumber, 5, line.Trim().Length));
                index++;
                continue;
            }

            if (key == "enabled")
            {
                if (!TryParseBool(value, out var parsedEnabled))
                {
                    diagnostics.Add(CreateError("enabled must be true or false", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    enabled = parsedEnabled;
                }

                index++;
                continue;
            }

            if (key == "severity")
            {
                if (!TryParseSeverity(value, out var parsedSeverity))
                {
                    diagnostics.Add(CreateError("severity must be one of info, warning, error", lineNumber, 5, line.Trim().Length));
                }
                else
                {
                    severity = parsedSeverity;
                }

                index++;
                continue;
            }

            diagnostics.Add(CreateError($"unknown rule option '{key}'", lineNumber, 5, key.Length));
            index++;
        }

        if (!ruleOptions.TryAdd(ruleId, new RuleOption(enabled, severity)))
        {
            diagnostics.Add(CreateError($"duplicate rule entry '{ruleId}'", ruleLineNumber, 3, ruleId.Length));
        }
    }

    void ParseAdditiveSection()
    {
        List<string>? dangerousEvents = null;
        List<string>? knownLabels = null;
        List<string>? registries = null;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 0)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 2)
            {
                diagnostics.Add(CreateError("additive customization key must be indented by 2 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseKey(line, out var key))
            {
                diagnostics.Add(CreateError("additive customization entry must be a key", lineNumber, 3, line.Trim().Length));
                index++;
                continue;
            }

            index++;
            var values = ParseListBlock(2, key);
            if (key == "additionalDangerousEvents")
            {
                dangerousEvents = values;
                continue;
            }

            if (key == "additionalKnownHostedLabels")
            {
                knownLabels = values;
                continue;
            }

            if (key == "additionalPublicRegistries")
            {
                registries = values;
                continue;
            }

            diagnostics.Add(CreateError($"unknown additive customization key '{key}'", lineNumber, 3, key.Length));
        }

        additiveCustomization = new RuleSpecificAdditiveCustomization(dangerousEvents, knownLabels, registries);
    }

    List<string> ParseListBlock(int parentIndent, string keyName)
    {
        var result = new List<string>();

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= parentIndent)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != parentIndent + 2)
            {
                diagnostics.Add(CreateError($"{keyName} list entry must be indented by {parentIndent + 2} spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                diagnostics.Add(CreateError($"{keyName} must be a YAML list", lineNumber, parentIndent + 3, trimmed.Length));
                index++;
                continue;
            }

            var value = trimmed[1..].Trim();
            result.Add(Unquote(value));
            index++;
        }

        return result;
    }

    void ParseExclusionsSection()
    {
        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 0)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 2)
            {
                diagnostics.Add(CreateError("exclusion entry must be indented by 2 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                diagnostics.Add(CreateError("exclusions must be a list", lineNumber, 3, trimmed.Length));
                index++;
                continue;
            }

            index++;
            ParseExclusionItem(lineNumber);
        }
    }

    void ParseExclusionItem(int lineNumber)
    {
        string? filePattern = null;
        string? jobId = null;
        List<string>? ruleIds = null;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 2)
            {
                break;
            }

            if (indent != 4)
            {
                diagnostics.Add(CreateError("exclusion field must be indented by 4 spaces", index + 1, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseProperty(line, out var key, out var value))
            {
                diagnostics.Add(CreateError("exclusion field must be key: value", index + 1, 5, line.Trim().Length));
                index++;
                continue;
            }

            if (key == "filePattern")
            {
                filePattern = Unquote(value);
                index++;
                continue;
            }

            if (key == "jobId")
            {
                jobId = Unquote(value);
                index++;
                continue;
            }

            if (key == "ruleIds")
            {
                index++;
                ruleIds = ParseListBlock(4, "ruleIds");
                continue;
            }

            diagnostics.Add(CreateError($"unknown exclusion field '{key}'", index + 1, 5, key.Length));
            index++;
        }

        if (string.IsNullOrWhiteSpace(filePattern))
        {
            diagnostics.Add(CreateError("exclusion filePattern is required", lineNumber, 3, 1));
            return;
        }

        if (ruleIds is null || ruleIds.Count == 0)
        {
            diagnostics.Add(CreateError("exclusion ruleIds is required", lineNumber, 3, 1));
            return;
        }

        exclusions.Add(new LintExclusion(filePattern, ruleIds, string.IsNullOrWhiteSpace(jobId) ? null : jobId));
    }

    void ParseExpressionContextSection()
    {
        List<string>? eventTypes = null;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent <= 0)
            {
                break;
            }

            var lineNumber = index + 1;
            if (indent != 2)
            {
                diagnostics.Add(CreateError("exprContext key must be indented by 2 spaces", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            if (!TryParseKey(line, out var key))
            {
                diagnostics.Add(CreateError("exprContext entry must be a key", lineNumber, 3, line.Trim().Length));
                index++;
                continue;
            }

            index++;
            if (key == "eventTypes")
            {
                eventTypes = ParseListBlock(2, "eventTypes");
                continue;
            }

            diagnostics.Add(CreateError($"unknown exprContext key '{key}'", lineNumber, 3, key.Length));
        }

        expressionContext = new ExpressionContext(eventTypes);
    }

    void SkipIndentedBlock(int parentIndent)
    {
        while (index < lines.Length)
        {
            var line = lines[index];
            if (TrySkip(line))
            {
                index++;
                continue;
            }

            if (GetIndent(line) <= parentIndent)
            {
                break;
            }

            index++;
        }
    }

    Diagnostic CreateError(string message, int line, int column, int length)
    {
        var safeLength = Math.Max(length, 1);
        return new Diagnostic(
            DiagnosticSeverity.Error,
            message,
            new TextRange(0, safeLength, line, column, line, column + safeLength),
            FilePath: filePath);
    }

    static bool TrySkip(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var trimmed = line.TrimStart();
        return trimmed.StartsWith("#", StringComparison.Ordinal);
    }

    static int GetIndent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    static bool TryParseKey(string line, out string key)
    {
        key = string.Empty;

        var trimmed = line.Trim();
        if (!trimmed.EndsWith(':'))
        {
            return false;
        }

        key = Unquote(trimmed[..^1].Trim());
        return key.Length > 0;
    }

    static bool TryParseProperty(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var trimmed = line.Trim();
        var colon = trimmed.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        key = Unquote(trimmed[..colon].Trim());
        value = trimmed[(colon + 1)..].Trim();
        return key.Length > 0;
    }

    static bool TryParseBool(string value, out bool result)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = default;
        return false;
    }

    static bool TryParseSeverity(string value, out DiagnosticSeverity severity)
    {
        if (string.Equals(value, "info", StringComparison.OrdinalIgnoreCase))
        {
            severity = DiagnosticSeverity.Info;
            return true;
        }

        if (string.Equals(value, "warning", StringComparison.OrdinalIgnoreCase))
        {
            severity = DiagnosticSeverity.Warning;
            return true;
        }

        if (string.Equals(value, "error", StringComparison.OrdinalIgnoreCase))
        {
            severity = DiagnosticSeverity.Error;
            return true;
        }

        severity = default;
        return false;
    }

    static bool TryParseInt(string value, out int result)
    {
        return int.TryParse(value, out result);
    }

    static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            if ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"'))
            {
                return value[1..^1];
            }
        }

        return value;
    }

    internal readonly record struct ParseResult(
        IReadOnlyDictionary<string, RuleOption> RuleOptions,
        RuleSpecificAdditiveCustomization AdditiveCustomization,
        IReadOnlyList<LintExclusion> Exclusions,
        ExpressionContext ExpressionContext,
        int? DefaultJobTimeoutMinutesForFix,
        PinResolutionConfig? PinResolution,
        OnlineAuditConfig? OnlineAudit,
        Diagnostic[] Diagnostics);
}
