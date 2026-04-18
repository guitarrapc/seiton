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
            return new LintResult(parseResult, parseResult.Diagnostics)
            {
                SuppressionSummary = SuppressionSummary.Empty,
            };
        }

        var diagnostics = new List<Diagnostic>(parseResult.Diagnostics.Length + 8);
        diagnostics.AddRange(parseResult.Diagnostics);

        var normalizedRules = NormalizeRules(config?.Rules, filePath);
        diagnostics.AddRange(normalizedRules.ConfigurationDiagnostics);

        var inlineSuppression = ParseInlineSuppression(utf8Yaml, filePath, parseResult.Workflow);
        diagnostics.AddRange(inlineSuppression.ConfigurationDiagnostics);

        var normalizedExclusions = NormalizeExclusions(config?.Exclusions, filePath, parseResult.Workflow, utf8Yaml);
        diagnostics.AddRange(normalizedExclusions.ConfigurationDiagnostics);

        if (rules.Count == 0)
        {
            return new LintResult(parseResult, diagnostics.ToArray())
            {
                SuppressionSummary = SuppressionSummary.Empty,
            };
        }

        var visitor = new WorkflowVisitor();
        var effectiveConfig = new LintConfig
        {
            Utf8Yaml = utf8Yaml,
            FilePath = filePath,
            Rules = normalizedRules.Rules,
            Fix = config?.Fix ?? new FixConfig(),
            Network = config?.Network ?? new NetworkConfig(),
        };

        var activeRules = new List<IRule>(rules.Count);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (!IsRuleEnabled(rule.Id, effectiveConfig.Rules))
            {
                continue;
            }

            rule.SetConfig(effectiveConfig);
            visitor.AddPass(rule);
            activeRules.Add(rule);
        }

        if (activeRules.Count == 0)
        {
            return new LintResult(parseResult, diagnostics.ToArray())
            {
                SuppressionSummary = SuppressionSummary.Empty,
            };
        }

        visitor.Visit(parseResult.Workflow);

        var ruleDiagnostics = new List<Diagnostic>(activeRules.Count * 4);
        for (var i = 0; i < activeRules.Count; i++)
        {
            var currentRuleDiagnostics = activeRules[i].GetDiagnostics();
            for (var j = 0; j < currentRuleDiagnostics.Length; j++)
            {
                var current = currentRuleDiagnostics[j];
                if (TryGetSeverityOverride(current.RuleId, effectiveConfig.Rules, out var severityOverride))
                {
                    current = current with { Severity = severityOverride };
                }

                ruleDiagnostics.Add(current);
            }
        }

        ruleDiagnostics.Sort(static (x, y) => CompareDiagnosticsByPriority(x, y));

        var seen = new HashSet<DiagnosticIdentity>();
        var suppressedByRule = new Dictionary<string, int>(StringComparer.Ordinal);
        var suppressionRecords = new List<SuppressionRecord>();
        for (var i = 0; i < ruleDiagnostics.Count; i++)
        {
            var current = ruleDiagnostics[i];
            var identity = new DiagnosticIdentity(current);
            if (!seen.Add(identity))
            {
                continue;
            }

            if (TryGetSuppressionRecord(current, inlineSuppression, normalizedExclusions.Exclusions, normalizedExclusions.NormalizedFilePath, out var suppressionRecord))
            {
                suppressionRecords.Add(suppressionRecord);
                if (!suppressedByRule.TryGetValue(suppressionRecord.RuleId, out var currentCount))
                {
                    suppressedByRule[suppressionRecord.RuleId] = 1;
                }
                else
                {
                    suppressedByRule[suppressionRecord.RuleId] = currentCount + 1;
                }

                continue;
            }

            diagnostics.Add(current);
        }

        return new LintResult(parseResult, diagnostics.ToArray())
        {
            SuppressionSummary = new SuppressionSummary(suppressionRecords.Count, suppressedByRule, suppressionRecords.ToArray()),
        };
    }

    static bool IsRuleEnabled(string? ruleId, IReadOnlyDictionary<string, RuleConfig>? rules)
    {
        if (!TryGetRuleConfig(ruleId, rules, out var ruleConfig))
        {
            return true;
        }

        return ruleConfig!.Enabled;
    }

    static bool TryGetSeverityOverride(string? ruleId, IReadOnlyDictionary<string, RuleConfig>? rules, out DiagnosticSeverity severity)
    {
        if (TryGetRuleConfig(ruleId, rules, out var ruleConfig) && ruleConfig?.Severity is not null)
        {
            severity = ruleConfig.Severity.Value;
            return true;
        }

        severity = default;
        return false;
    }

    static bool TryGetRuleConfig(string? ruleId, IReadOnlyDictionary<string, RuleConfig>? rules, out RuleConfig? config)
    {
        config = null;
        if (string.IsNullOrEmpty(ruleId) || rules is null || rules.Count == 0)
        {
            return false;
        }

        if (rules.TryGetValue(ruleId, out config))
        {
            return true;
        }

        if (!RuleCatalog.TryResolveRuleId(ruleId, out var resolvedRuleId))
        {
            return false;
        }

        return rules.TryGetValue(resolvedRuleId, out config);
    }

    static bool TryGetSuppressionRecord(
        Diagnostic diagnostic,
        InlineSuppression inlineSuppression,
        IReadOnlyList<NormalizedExclusion> normalizedExclusions,
        string normalizedFilePath,
        out SuppressionRecord suppressionRecord)
    {
        if (TryGetInlineSuppressionRecord(diagnostic, inlineSuppression, out suppressionRecord))
        {
            return true;
        }

        return TryGetConfigSuppressionRecord(diagnostic, inlineSuppression.JobScopes, normalizedExclusions, normalizedFilePath, out suppressionRecord);
    }

    static bool TryGetInlineSuppressionRecord(Diagnostic diagnostic, InlineSuppression inlineSuppression, out SuppressionRecord suppressionRecord)
    {
        if (diagnostic.RuleId is null)
        {
            suppressionRecord = default;
            return false;
        }

        if (inlineSuppression.FileRuleSuppressions.TryGetValue(diagnostic.RuleId, out var fileAnchor))
        {
            suppressionRecord = new SuppressionRecord(
                diagnostic.RuleId,
                SuppressionSource.InlineFile,
                fileAnchor.Line,
                fileAnchor.Column,
                diagnostic.Location.StartLine,
                diagnostic.Location.StartColumn);
            return true;
        }

        if (inlineSuppression.NextLineRuleSuppressions.TryGetValue(diagnostic.Location.StartLine, out var nextLineSuppressedRuleIds)
            && nextLineSuppressedRuleIds.TryGetValue(diagnostic.RuleId, out var nextLineAnchor))
        {
            suppressionRecord = new SuppressionRecord(
                diagnostic.RuleId,
                SuppressionSource.InlineNextLine,
                nextLineAnchor.Line,
                nextLineAnchor.Column,
                diagnostic.Location.StartLine,
                diagnostic.Location.StartColumn);
            return true;
        }

        if (inlineSuppression.JobRuleSuppressions.Count == 0)
        {
            suppressionRecord = default;
            return false;
        }

        if (!TryFindJobIdForLine(diagnostic.Location.StartLine, inlineSuppression.JobScopes, out var jobId))
        {
            suppressionRecord = default;
            return false;
        }

        if (inlineSuppression.JobRuleSuppressions.TryGetValue(jobId, out var jobSuppressedRuleIds)
            && jobSuppressedRuleIds.TryGetValue(diagnostic.RuleId, out var jobAnchor))
        {
            suppressionRecord = new SuppressionRecord(
                diagnostic.RuleId,
                SuppressionSource.InlineJob,
                jobAnchor.Line,
                jobAnchor.Column,
                diagnostic.Location.StartLine,
                diagnostic.Location.StartColumn);
            return true;
        }

        suppressionRecord = default;
        return false;
    }

    static bool TryGetConfigSuppressionRecord(
        Diagnostic diagnostic,
        IReadOnlyList<JobScope> jobScopes,
        IReadOnlyList<NormalizedExclusion> normalizedExclusions,
        string normalizedFilePath,
        out SuppressionRecord suppressionRecord)
    {
        suppressionRecord = default;
        if (diagnostic.RuleId is null || normalizedExclusions.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < normalizedExclusions.Count; i++)
        {
            var exclusion = normalizedExclusions[i];
            if (!GlobMatch(exclusion.Files, normalizedFilePath))
            {
                continue;
            }

            if (!exclusion.Rules.Contains(diagnostic.RuleId))
            {
                continue;
            }

            if (exclusion.Jobs is null || exclusion.Jobs.Count == 0)
            {
                suppressionRecord = new SuppressionRecord(
                    diagnostic.RuleId,
                    SuppressionSource.ConfigFile,
                    1,
                    1,
                    diagnostic.Location.StartLine,
                    diagnostic.Location.StartColumn);
                return true;
            }

            if (!TryFindJobIdForLine(diagnostic.Location.StartLine, jobScopes, out var jobId))
            {
                continue;
            }

            var jobMatched = false;
            for (var j = 0; j < exclusion.Jobs.Count; j++)
            {
                if (string.Equals(jobId, exclusion.Jobs[j], StringComparison.OrdinalIgnoreCase))
                {
                    jobMatched = true;
                    break;
                }
            }

            if (!jobMatched)
            {
                continue;
            }

            suppressionRecord = new SuppressionRecord(
                diagnostic.RuleId,
                SuppressionSource.ConfigJob,
                1,
                1,
                diagnostic.Location.StartLine,
                diagnostic.Location.StartColumn);
            return true;
        }

        return false;
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
        var nextLineRuleSuppressions = new Dictionary<int, Dictionary<string, SuppressionAnchor>>();
        var fileRuleSuppressions = new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal);
        var jobRuleSuppressions = new Dictionary<string, Dictionary<string, SuppressionAnchor>>(StringComparer.OrdinalIgnoreCase);
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
                        suppressedRuleIds = new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal);
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
                    suppressedRuleIds = new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal);
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
        Dictionary<string, SuppressionAnchor> target,
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
            var tokenColumn = FindTokenColumn(lineCore, ruleIdToken, commentIndex);
            if (RuleCatalog.TryResolveRuleId(ruleIdToken, out var internalRuleId))
            {
                if (RuleCatalog.IsNonDisableable(internalRuleId))
                {
                    var nonDisableableTokenStart = lineStartOffset + tokenColumn - 1;
                    configurationDiagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        $"rule '{internalRuleId}' is non-disableable",
                        new TextRange(nonDisableableTokenStart, ruleIdToken.Length, lineNumber, tokenColumn, lineNumber, tokenColumn + ruleIdToken.Length),
                        FilePath: filePath));
                    continue;
                }

                target[internalRuleId] = new SuppressionAnchor(lineNumber, tokenColumn);
                continue;
            }

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

    static RulesNormalization NormalizeRules(IReadOnlyDictionary<string, RuleConfig>? rules, string filePath)
    {
        if (rules is null || rules.Count == 0)
        {
            return RulesNormalization.Empty;
        }

        var normalized = new Dictionary<string, RuleConfig>(StringComparer.Ordinal);
        var diagnostics = new List<Diagnostic>();
        foreach (var pair in rules)
        {
            if (RuleCatalog.TryResolveRuleId(pair.Key, out var resolvedRuleId))
            {
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
                continue;
            }

            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                BuildUnknownRuleIdMessage(pair.Key),
                new TextRange(0, pair.Key.Length, 1, 1, 1, 1 + pair.Key.Length),
                FilePath: filePath));
        }

        return new RulesNormalization(normalized, diagnostics.ToArray());
    }

    static ExclusionsNormalization NormalizeExclusions(IReadOnlyList<LintExclusion>? exclusions, string filePath, Parsing.Ast.Workflow workflow, byte[] utf8Yaml)
    {
        var normalizedFilePath = NormalizePath(filePath);
        if (exclusions is null || exclusions.Count == 0)
        {
            return new ExclusionsNormalization([], normalizedFilePath, []);
        }

        var knownJobIds = BuildKnownJobIds(workflow, utf8Yaml);
        var normalized = new List<NormalizedExclusion>(exclusions.Count);
        var diagnostics = new List<Diagnostic>();

        for (var i = 0; i < exclusions.Count; i++)
        {
            var exclusion = exclusions[i];
            if (string.IsNullOrWhiteSpace(exclusion.Files))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "exclusion files pattern must not be empty",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            var normalizedRuleIds = new HashSet<string>(StringComparer.Ordinal);
            var ruleIds = exclusion.Rules;
            for (var j = 0; j < ruleIds.Count; j++)
            {
                var ruleId = ruleIds[j];
                if (RuleCatalog.TryResolveRuleId(ruleId, out var resolvedRuleId))
                {
                    if (RuleCatalog.IsNonDisableable(resolvedRuleId))
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticSeverity.Error,
                            $"rule '{resolvedRuleId}' is non-disableable",
                            new TextRange(0, ruleId.Length, 1, 1, 1, 1 + ruleId.Length),
                            FilePath: filePath));
                        continue;
                    }

                    normalizedRuleIds.Add(resolvedRuleId);
                    continue;
                }

                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    BuildUnknownRuleIdMessage(ruleId),
                    new TextRange(0, ruleId.Length, 1, 1, 1, 1 + ruleId.Length),
                    FilePath: filePath));
            }

            if (normalizedRuleIds.Count == 0)
            {
                continue;
            }

            if (exclusion.Jobs is not null)
            {
                for (var j = 0; j < exclusion.Jobs.Count; j++)
                {
                    var jobId = exclusion.Jobs[j];
                    if (!string.IsNullOrEmpty(jobId) && !knownJobIds.Contains(jobId))
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticSeverity.Error,
                            $"unknown job-id '{jobId}' in exclusion configuration",
                            new TextRange(0, jobId.Length, 1, 1, 1, 1 + jobId.Length),
                            FilePath: filePath));
                    }
                }
            }

            normalized.Add(new NormalizedExclusion(NormalizePath(exclusion.Files), normalizedRuleIds, exclusion.Jobs));
        }

        return new ExclusionsNormalization(normalized, normalizedFilePath, diagnostics.ToArray());
    }

    static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    static bool GlobMatch(string pattern, string path)
    {
        if (pattern.Length == 0)
        {
            return path.Length == 0;
        }

        var normalizedPattern = NormalizePath(pattern);
        var normalizedPath = NormalizePath(path);
        var cache = new Dictionary<(int PatternIndex, int PathIndex), bool>();
        return GlobMatchCore(normalizedPattern, normalizedPath, 0, 0, cache);
    }

    static bool GlobMatchCore(
        string pattern,
        string path,
        int patternIndex,
        int pathIndex,
        Dictionary<(int PatternIndex, int PathIndex), bool> cache)
    {
        if (cache.TryGetValue((patternIndex, pathIndex), out var cached))
        {
            return cached;
        }

        var patternLength = pattern.Length;
        var pathLength = path.Length;

        while (patternIndex < patternLength)
        {
            var ch = pattern[patternIndex];
            if (ch == '*')
            {
                var isDoubleStar = patternIndex + 1 < patternLength && pattern[patternIndex + 1] == '*';
                if (isDoubleStar)
                {
                    patternIndex += 2;
                    while (patternIndex < patternLength && pattern[patternIndex] == '*')
                    {
                        patternIndex++;
                    }

                    if (patternIndex >= patternLength)
                    {
                        cache[(patternIndex, pathIndex)] = true;
                        return true;
                    }

                    for (var cursor = pathIndex; cursor <= pathLength; cursor++)
                    {
                        if (GlobMatchCore(pattern, path, patternIndex, cursor, cache))
                        {
                            cache[(patternIndex, pathIndex)] = true;
                            return true;
                        }
                    }

                    cache[(patternIndex, pathIndex)] = false;
                    return false;
                }

                patternIndex++;
                for (var cursor = pathIndex; ; cursor++)
                {
                    if (GlobMatchCore(pattern, path, patternIndex, cursor, cache))
                    {
                        cache[(patternIndex, pathIndex)] = true;
                        return true;
                    }

                    if (cursor >= pathLength || path[cursor] == '/')
                    {
                        break;
                    }
                }

                cache[(patternIndex, pathIndex)] = false;
                return false;
            }

            if (pathIndex >= pathLength)
            {
                cache[(patternIndex, pathIndex)] = false;
                return false;
            }

            if (ch == '?')
            {
                if (path[pathIndex] == '/')
                {
                    cache[(patternIndex, pathIndex)] = false;
                    return false;
                }

                patternIndex++;
                pathIndex++;
                continue;
            }

            if (ch != path[pathIndex])
            {
                cache[(patternIndex, pathIndex)] = false;
                return false;
            }

            patternIndex++;
            pathIndex++;
        }

        var result = pathIndex == pathLength;
        cache[(patternIndex, pathIndex)] = result;
        return result;
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
        IReadOnlyDictionary<int, Dictionary<string, SuppressionAnchor>> NextLineRuleSuppressions,
        IReadOnlyDictionary<string, SuppressionAnchor> FileRuleSuppressions,
        IReadOnlyDictionary<string, Dictionary<string, SuppressionAnchor>> JobRuleSuppressions,
        IReadOnlyList<JobScope> JobScopes,
        Diagnostic[] ConfigurationDiagnostics)
    {
        public static InlineSuppression Empty { get; } = new(
            new Dictionary<int, Dictionary<string, SuppressionAnchor>>(),
            new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal),
            new Dictionary<string, Dictionary<string, SuppressionAnchor>>(StringComparer.Ordinal),
            [],
            []);
    }

    readonly record struct JobScope(string JobId, int StartLine, int EndLine);

    readonly record struct SuppressionAnchor(int Line, int Column);

    readonly record struct RulesNormalization(
        IReadOnlyDictionary<string, RuleConfig>? Rules,
        Diagnostic[] ConfigurationDiagnostics)
    {
        public static RulesNormalization Empty { get; } = new(null, []);
    }

    readonly record struct ExclusionsNormalization(
        IReadOnlyList<NormalizedExclusion> Exclusions,
        string NormalizedFilePath,
        Diagnostic[] ConfigurationDiagnostics)
    {
        public static ExclusionsNormalization Empty { get; } = new([], string.Empty, []);
    }

    readonly record struct NormalizedExclusion(
        string Files,
        IReadOnlySet<string> Rules,
        IReadOnlyList<string>? Jobs);
}
