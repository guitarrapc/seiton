using System.Text;
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

        var config = new LintConfig
        {
            Utf8Yaml = Encoding.UTF8.GetBytes(yamlText),
            FilePath = filePath,
            RuleOptions = normalizedRuleOptions.RuleOptions,
            Exclusions = normalizedExclusions.Exclusions,
            ExprContext = parseResult.ExpressionContext,
            AdditiveCustomization = normalizedAdditive.AdditiveCustomization,
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

            if (!TryParseKey(line, out var key))
            {
                diagnostics.Add(CreateError("expected mapping key", lineNumber, indent + 1, line.Trim().Length));
                index++;
                continue;
            }

            index++;
            if (key == "rules")
            {
                ParseRulesSection();
                continue;
            }

            if (key == "additiveCustomization")
            {
                ParseAdditiveSection();
                continue;
            }

            if (key == "exclusions")
            {
                ParseExclusionsSection();
                continue;
            }

            if (key == "exprContext")
            {
                ParseExpressionContextSection();
                continue;
            }

            diagnostics.Add(CreateError($"unknown top-level key '{key}'", lineNumber, 1, key.Length));
            SkipIndentedBlock(0);
        }

        return new ParseResult(
            new Dictionary<string, RuleOption>(ruleOptions, StringComparer.Ordinal),
            additiveCustomization,
            exclusions.ToArray(),
            expressionContext,
            diagnostics.ToArray());
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
        Diagnostic[] Diagnostics);
}
