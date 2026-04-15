using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

public sealed class LintEngine
{
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

        if (rules.Count == 0)
        {
            return new LintResult(parseResult, diagnostics.ToArray());
        }

        var visitor = new WorkflowVisitor();
        var effectiveConfig = new LintConfig
        {
            Utf8Yaml = utf8Yaml,
            FilePath = filePath,
            RuleOptions = config?.RuleOptions,
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

        var ruleIdValue = ruleId;

        if (options.TryGetValue(ruleIdValue, out option))
        {
            return true;
        }

        foreach (var pair in options)
        {
            if (!string.Equals(pair.Key, ruleIdValue, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            option = pair.Value;
            return true;
        }

        return false;
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
}
