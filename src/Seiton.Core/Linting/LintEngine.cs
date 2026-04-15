using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Linting;

public sealed class LintEngine
{
    const string InlineDirectivePrefix = "seiton:";
    const string DisableNextLineCommand = "disable-next-line";
    const string DisableJobCommand = "disable-job";
    const string DisableFileCommand = "disable-file";

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

        var inlineSuppression = ParseInlineSuppression(utf8Yaml, filePath, parseResult.Workflow);
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

            if (IsInlineSuppressed(current, inlineSuppression))
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

    static bool IsInlineSuppressed(Diagnostic diagnostic, InlineSuppression inlineSuppression)
    {
        if (diagnostic.RuleId is null)
        {
            return false;
        }

        if (inlineSuppression.FileRuleSuppressions.Contains(diagnostic.RuleId))
        {
            return true;
        }

        if (inlineSuppression.NextLineRuleSuppressions.TryGetValue(diagnostic.Location.StartLine, out var nextLineSuppressedRuleIds)
            && nextLineSuppressedRuleIds.Contains(diagnostic.RuleId))
        {
            return true;
        }

        if (inlineSuppression.JobRuleSuppressions.Count == 0)
        {
            return false;
        }

        if (!TryFindJobIdForLine(diagnostic.Location.StartLine, inlineSuppression.JobScopes, out var jobId))
        {
            return false;
        }

        return inlineSuppression.JobRuleSuppressions.TryGetValue(jobId, out var jobSuppressedRuleIds)
            && jobSuppressedRuleIds.Contains(diagnostic.RuleId);
    }

    static bool TryFindJobIdForLine(int line, IReadOnlyList<JobScope> jobScopes, out string jobId)
    {
        for (var i = 0; i < jobScopes.Count; i++)
        {
            var scope = jobScopes[i];
            if (line >= scope.StartLine && line <= scope.EndLine)
            {
                jobId = scope.JobId;
                return true;
            }
        }

        jobId = string.Empty;
        return false;
    }

    static InlineSuppression ParseInlineSuppression(byte[] utf8Yaml, string filePath, Parsing.Ast.Workflow workflow)
    {
        if (utf8Yaml.Length == 0)
        {
            return InlineSuppression.Empty;
        }

        var knownJobIds = BuildKnownJobIds(workflow, utf8Yaml);
        var jobScopes = BuildJobScopes(workflow, utf8Yaml);
        var text = Encoding.UTF8.GetString(utf8Yaml);
        var lines = text.Split('\n');
        var nextLineRuleSuppressions = new Dictionary<int, HashSet<string>>();
        var fileRuleSuppressions = new HashSet<string>(StringComparer.Ordinal);
        var jobRuleSuppressions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
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
            var command = string.Empty;
            var arguments = string.Empty;

            if (commentText.StartsWith(InlineDirectivePrefix, StringComparison.Ordinal))
            {
                var commandAndArgs = commentText[InlineDirectivePrefix.Length..].TrimStart();
                if (commandAndArgs.Length == 0)
                {
                    lineStartOffset += line.Length + 1;
                    continue;
                }

                var separator = commandAndArgs.IndexOfAny(' ', '\t');
                if (separator < 0)
                {
                    command = commandAndArgs;
                }
                else
                {
                    command = commandAndArgs[..separator];
                    arguments = commandAndArgs[(separator + 1)..].Trim();
                }
            }
            else
            {
                lineStartOffset += line.Length + 1;
                continue;
            }

            if (string.Equals(command, DisableNextLineCommand, StringComparison.Ordinal))
            {
                if (arguments.Length > 0)
                {
                    var targetLine = lineNumber + 1;
                    if (!nextLineRuleSuppressions.TryGetValue(targetLine, out var suppressedRuleIds))
                    {
                        suppressedRuleIds = new HashSet<string>(StringComparer.Ordinal);
                        nextLineRuleSuppressions[targetLine] = suppressedRuleIds;
                    }

                    AddRuleIds(arguments, suppressedRuleIds, configurationDiagnostics, filePath, lineCore, lineStartOffset, lineNumber, commentIndex);
                }

                lineStartOffset += line.Length + 1;
                continue;
            }

            if (string.Equals(command, DisableFileCommand, StringComparison.Ordinal))
            {
                if (arguments.Length > 0)
                {
                    AddRuleIds(arguments, fileRuleSuppressions, configurationDiagnostics, filePath, lineCore, lineStartOffset, lineNumber, commentIndex);
                }

                lineStartOffset += line.Length + 1;
                continue;
            }

            if (string.Equals(command, DisableJobCommand, StringComparison.Ordinal))
            {
                var separator = arguments.IndexOfAny(' ', '\t');
                if (separator <= 0)
                {
                    configurationDiagnostics.Add(BuildInlineDirectiveError(
                        "disable-job requires <job-id> and <rule-id list>",
                        filePath,
                        lineStartOffset,
                        lineNumber,
                        lineCore,
                        commentIndex,
                        command));
                    lineStartOffset += line.Length + 1;
                    continue;
                }

                var jobId = arguments[..separator].Trim();
                var ruleIdList = arguments[(separator + 1)..].Trim();
                if (ruleIdList.Length == 0)
                {
                    configurationDiagnostics.Add(BuildInlineDirectiveError(
                        "disable-job requires at least one rule-id",
                        filePath,
                        lineStartOffset,
                        lineNumber,
                        lineCore,
                        commentIndex,
                        command));
                    lineStartOffset += line.Length + 1;
                    continue;
                }

                if (!knownJobIds.Contains(jobId))
                {
                    var jobColumn = FindTokenColumn(lineCore, jobId, commentIndex);
                    var jobStart = lineStartOffset + jobColumn - 1;
                    configurationDiagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        $"unknown job-id '{jobId}' in inline suppression directive",
                        new TextRange(jobStart, jobId.Length, lineNumber, jobColumn, lineNumber, jobColumn + jobId.Length),
                        FilePath: filePath));
                    lineStartOffset += line.Length + 1;
                    continue;
                }

                if (!jobRuleSuppressions.TryGetValue(jobId, out var suppressedRuleIds))
                {
                    suppressedRuleIds = new HashSet<string>(StringComparer.Ordinal);
                    jobRuleSuppressions[jobId] = suppressedRuleIds;
                }

                AddRuleIds(ruleIdList, suppressedRuleIds, configurationDiagnostics, filePath, lineCore, lineStartOffset, lineNumber, commentIndex);
                lineStartOffset += line.Length + 1;
                continue;
            }

            configurationDiagnostics.Add(BuildInlineDirectiveError(
                $"unknown inline suppression command '{command}'",
                filePath,
                lineStartOffset,
                lineNumber,
                lineCore,
                commentIndex,
                command));

            lineStartOffset += line.Length + 1;
        }

        return new InlineSuppression(
            nextLineRuleSuppressions,
            fileRuleSuppressions,
            jobRuleSuppressions,
            jobScopes,
            configurationDiagnostics.ToArray());
    }

    static Diagnostic BuildInlineDirectiveError(string message, string filePath, int lineStartOffset, int lineNumber, string lineCore, int commentIndex, string token)
    {
        var tokenColumn = token.Length == 0 ? commentIndex + 2 : FindTokenColumn(lineCore, token, commentIndex);
        var tokenStart = lineStartOffset + tokenColumn - 1;
        var tokenLength = token.Length == 0 ? 1 : token.Length;

        return new Diagnostic(
            DiagnosticSeverity.Error,
            message,
            new TextRange(tokenStart, tokenLength, lineNumber, tokenColumn, lineNumber, tokenColumn + tokenLength),
            FilePath: filePath);
    }

    static HashSet<string> BuildKnownJobIds(Parsing.Ast.Workflow workflow, byte[] utf8Yaml)
    {
        var jobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in workflow.Jobs)
        {
            var span = pair.Value.Id.Value.AsSpan(utf8Yaml);
            if (span.Length == 0)
            {
                continue;
            }

            var jobId = Encoding.UTF8.GetString(span);
            if (jobId.Length == 0)
            {
                continue;
            }

            jobIds.Add(jobId);
        }

        return jobIds;
    }

    static IReadOnlyList<JobScope> BuildJobScopes(Parsing.Ast.Workflow workflow, byte[] utf8Yaml)
    {
        var scopes = new List<JobScope>(workflow.Jobs.Count);
        foreach (var pair in workflow.Jobs)
        {
            var span = pair.Value.Id.Value.AsSpan(utf8Yaml);
            if (span.Length == 0)
            {
                continue;
            }

            var range = pair.Value.Range;
            if (range.StartLine <= 0 || range.EndLine <= 0)
            {
                continue;
            }

            var jobId = Encoding.UTF8.GetString(span);
            if (jobId.Length == 0)
            {
                continue;
            }

            scopes.Add(new JobScope(jobId, range.StartLine, range.EndLine));
        }

        return scopes;
    }

    static void AddRuleIds(
        string ruleIdList,
        HashSet<string> target,
        List<Diagnostic> configurationDiagnostics,
        string filePath,
        string lineCore,
        int lineStartOffset,
        int lineNumber,
        int commentIndex)
    {
        var ruleIds = ruleIdList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < ruleIds.Length; i++)
        {
            var ruleIdToken = ruleIds[i];
            if (RuleCatalog.TryResolveRuleId(ruleIdToken, out var internalRuleId))
            {
                target.Add(internalRuleId);
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
        IReadOnlySet<string> FileRuleSuppressions,
        IReadOnlyDictionary<string, HashSet<string>> JobRuleSuppressions,
        IReadOnlyList<JobScope> JobScopes,
        Diagnostic[] ConfigurationDiagnostics)
    {
        public static InlineSuppression Empty { get; } = new(
            new Dictionary<int, HashSet<string>>(),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
            [],
            []);
    }

    readonly record struct JobScope(string JobId, int StartLine, int EndLine);

    readonly record struct RuleOptionsNormalization(
        IReadOnlyDictionary<string, RuleOption>? RuleOptions,
        Diagnostic[] ConfigurationDiagnostics)
    {
        public static RuleOptionsNormalization Empty { get; } = new(null, []);
    }
}
