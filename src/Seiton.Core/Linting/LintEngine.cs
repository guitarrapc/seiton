using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Linting;

public sealed class LintEngine
{
    const string InlineDisableNextLinePrefix = "seiton-lint: disable-next-line";

    readonly List<IRule> rules = [];

    public LintEngine()
    {
        rules.AddRange(RuleCatalog.CreateDefaultRules());
    }

    public LintEngine(IEnumerable<IRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (var rule in rules)
        {
            AddRule(rule);
        }
    }

    public void AddRule(IRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rules.Add(rule);
    }

    public LintResult Check(byte[] utf8Yaml, string filePath)
    {
        return Check(utf8Yaml, filePath, config: null);
    }

    public LintResult Check(byte[] utf8Yaml, string filePath, LintConfig? config)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var parseResult = WorkflowParser.Parse(utf8Yaml, filePath);
        if (parseResult.HasFatalError || parseResult.Workflow is null)
        {
            return new LintResult(parseResult, parseResult.Diagnostics);
        }

        var diagnostics = new List<Diagnostic>(parseResult.Diagnostics.Length + 8);
        diagnostics.AddRange(parseResult.Diagnostics);

        var normalizedRuleOptions = NormalizeRuleOptions(config?.RuleOptions, filePath);
        diagnostics.AddRange(normalizedRuleOptions.ConfigurationDiagnostics);

        var inlineSuppression = ParseInlineSuppression(utf8Yaml, filePath);
        diagnostics.AddRange(inlineSuppression.ConfigurationDiagnostics);

        if (rules.Count == 0)
        {
            return new LintResult(parseResult, diagnostics.ToArray());
        }

        var visitor = new WorkflowVisitor();
        var effectiveConfig = new LintConfig
        {
            Utf8Yaml = utf8Yaml,
            FilePath = filePath,
            RuleOptions = normalizedRuleOptions.RuleOptions,
        };

        var activeRules = new List<IRule>(rules.Count);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (!IsRuleEnabled(rule.Id, effectiveConfig.RuleOptions))
            {
                continue;
            }

            rule.SetConfig(effectiveConfig);
            visitor.AddPass(rule);
            activeRules.Add(rule);
        }

        if (activeRules.Count == 0)
        {
            return new LintResult(parseResult, diagnostics.ToArray());
        }

        visitor.Visit(parseResult.Workflow);

        var ruleDiagnostics = new List<Diagnostic>(activeRules.Count * 4);
        for (var i = 0; i < activeRules.Count; i++)
        {
            var currentRuleDiagnostics = activeRules[i].GetDiagnostics();
            for (var j = 0; j < currentRuleDiagnostics.Length; j++)
            {
                var current = currentRuleDiagnostics[j];
                if (TryGetSeverityOverride(current.RuleId, effectiveConfig.RuleOptions, out var severityOverride))
                {
                    current = current with { Severity = severityOverride };
                }

                ruleDiagnostics.Add(current);
            }
        }

        ruleDiagnostics.Sort(static (x, y) => CompareDiagnosticsByPriority(x, y));

        var seen = new HashSet<DiagnosticIdentity>();
        for (var i = 0; i < ruleDiagnostics.Count; i++)
        {
            var current = ruleDiagnostics[i];
            var identity = new DiagnosticIdentity(current);
            if (!seen.Add(identity))
            {
                continue;
            }

            if (IsInlineSuppressed(current, inlineSuppression.NextLineRuleSuppressions))
            {
                continue;
            }

            diagnostics.Add(current);
        }

        return new LintResult(parseResult, diagnostics.ToArray());
    }

    static bool IsRuleEnabled(string? ruleId, IReadOnlyDictionary<string, RuleOption>? options)
    {
        if (!TryGetRuleOption(ruleId, options, out var ruleOption))
        {
            return true;
        }

        return ruleOption!.Enabled;
    }

    static bool TryGetSeverityOverride(string? ruleId, IReadOnlyDictionary<string, RuleOption>? options, out DiagnosticSeverity severity)
    {
        if (TryGetRuleOption(ruleId, options, out var ruleOption) && ruleOption?.Severity is not null)
        {
            severity = ruleOption.Severity.Value;
            return true;
        }

        severity = default;
        return false;
    }

    static bool TryGetRuleOption(string? ruleId, IReadOnlyDictionary<string, RuleOption>? options, out RuleOption? option)
    {
        option = null;
        if (string.IsNullOrEmpty(ruleId) || options is null || options.Count == 0)
        {
            return false;
        }

        if (options.TryGetValue(ruleId, out option))
        {
            return true;
        }

        if (!RuleCatalog.TryResolveRuleId(ruleId, out var resolvedRuleId))
        {
            return false;
        }

        return options.TryGetValue(resolvedRuleId, out option);
    }

    static bool IsInlineSuppressed(Diagnostic diagnostic, IReadOnlyDictionary<int, HashSet<string>> nextLineRuleSuppressions)
    {
        if (diagnostic.RuleId is null || nextLineRuleSuppressions.Count == 0)
        {
            return false;
        }

        if (!nextLineRuleSuppressions.TryGetValue(diagnostic.Location.StartLine, out var suppressedRuleIds))
        {
            return false;
        }

        return suppressedRuleIds.Contains(diagnostic.RuleId);
    }

    static InlineSuppression ParseInlineSuppression(byte[] utf8Yaml, string filePath)
    {
        if (utf8Yaml.Length == 0)
        {
            return InlineSuppression.Empty;
        }

        var text = Encoding.UTF8.GetString(utf8Yaml);
        var lines = text.Split('\n');
        var nextLineRuleSuppressions = new Dictionary<int, HashSet<string>>();
        var configurationDiagnostics = new List<Diagnostic>();

        var lineStartOffset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = lines[i];
            var lineCore = line.EndsWith("\r", StringComparison.Ordinal) ? line[..^1] : line;
            var commentIndex = lineCore.IndexOf('#');
            if (commentIndex < 0)
            {
                lineStartOffset += line.Length + 1;
                continue;
            }

            var commentText = lineCore[(commentIndex + 1)..].TrimStart();
            if (!commentText.StartsWith(InlineDisableNextLinePrefix, StringComparison.Ordinal))
            {
                lineStartOffset += line.Length + 1;
                continue;
            }

            var ruleIdList = commentText[InlineDisableNextLinePrefix.Length..].Trim();
            if (ruleIdList.Length == 0)
            {
                lineStartOffset += line.Length + 1;
                continue;
            }

            var targetLine = lineNumber + 1;
            if (!nextLineRuleSuppressions.TryGetValue(targetLine, out var suppressedRuleIds))
            {
                suppressedRuleIds = new HashSet<string>(StringComparer.Ordinal);
                nextLineRuleSuppressions[targetLine] = suppressedRuleIds;
            }

            var ruleIds = ruleIdList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (var j = 0; j < ruleIds.Length; j++)
            {
                var ruleIdToken = ruleIds[j];
                if (RuleCatalog.TryResolveRuleId(ruleIdToken, out var internalRuleId))
                {
                    suppressedRuleIds.Add(internalRuleId);
                    continue;
                }

                var tokenColumn = FindTokenColumn(lineCore, ruleIdToken, commentIndex);
                var tokenStart = lineStartOffset + tokenColumn - 1;
                configurationDiagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    BuildUnknownRuleIdMessage(ruleIdToken),
                    new TextRange(tokenStart, ruleIdToken.Length, lineNumber, tokenColumn, lineNumber, tokenColumn + ruleIdToken.Length),
                    FilePath: filePath));
            }

            lineStartOffset += line.Length + 1;
        }

        return new InlineSuppression(nextLineRuleSuppressions, configurationDiagnostics.ToArray());
    }

    static int FindTokenColumn(string line, string token, int fallbackStart)
    {
        var tokenStart = line.IndexOf(token, fallbackStart, StringComparison.Ordinal);
        return tokenStart >= 0 ? tokenStart + 1 : fallbackStart + 2;
    }

    static RuleOptionsNormalization NormalizeRuleOptions(IReadOnlyDictionary<string, RuleOption>? options, string filePath)
    {
        if (options is null || options.Count == 0)
        {
            return RuleOptionsNormalization.Empty;
        }

        var normalized = new Dictionary<string, RuleOption>(StringComparer.Ordinal);
        var diagnostics = new List<Diagnostic>();
        foreach (var pair in options)
        {
            if (RuleCatalog.TryResolveRuleId(pair.Key, out var resolvedRuleId))
            {
                normalized[resolvedRuleId] = pair.Value;
                continue;
            }

            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                BuildUnknownRuleIdMessage(pair.Key),
                new TextRange(0, pair.Key.Length, 1, 1, 1, 1 + pair.Key.Length),
                FilePath: filePath));
        }

        return new RuleOptionsNormalization(normalized, diagnostics.ToArray());
    }

    static string BuildUnknownRuleIdMessage(string unknownRuleId)
    {
        var suggested = RuleCatalog.SuggestRuleId(unknownRuleId);
        return suggested is null
            ? $"unknown rule-id '{unknownRuleId}'"
            : $"unknown rule-id '{unknownRuleId}'. Did you mean '{suggested}'?";
    }

    static int CompareDiagnosticsByPriority(Diagnostic x, Diagnostic y)
    {
        var byPriority = RuleCatalog.GetPriority(x.RuleId).CompareTo(RuleCatalog.GetPriority(y.RuleId));
        if (byPriority != 0)
        {
            return byPriority;
        }

        var bySeverity = x.Severity.CompareTo(y.Severity);
        if (bySeverity != 0)
        {
            return bySeverity;
        }

        var byLine = x.Location.StartLine.CompareTo(y.Location.StartLine);
        if (byLine != 0)
        {
            return byLine;
        }

        var byColumn = x.Location.StartColumn.CompareTo(y.Location.StartColumn);
        if (byColumn != 0)
        {
            return byColumn;
        }

        return string.CompareOrdinal(x.Message, y.Message);
    }

    readonly record struct DiagnosticIdentity(
        DiagnosticSeverity Severity,
        string Message,
        int Start,
        int Length,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn)
    {
        public DiagnosticIdentity(Diagnostic diagnostic)
            : this(
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.Location.Start,
                diagnostic.Location.Length,
                diagnostic.Location.StartLine,
                diagnostic.Location.StartColumn,
                diagnostic.Location.EndLine,
                diagnostic.Location.EndColumn)
        {
        }
    }

    readonly record struct InlineSuppression(
        IReadOnlyDictionary<int, HashSet<string>> NextLineRuleSuppressions,
        Diagnostic[] ConfigurationDiagnostics)
    {
        public static InlineSuppression Empty { get; } = new(
            new Dictionary<int, HashSet<string>>(),
            []);
    }

    readonly record struct RuleOptionsNormalization(
        IReadOnlyDictionary<string, RuleOption>? RuleOptions,
        Diagnostic[] ConfigurationDiagnostics)
    {
        public static RuleOptionsNormalization Empty { get; } = new(null, []);
    }
}
