using Seiton.Core.Parsing;
using System.Runtime.CompilerServices;
using System.Text;

using static Seiton.Core.Linting.ActionRefHelpers;

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

        var classifiedParseResult = WorkflowParser.ParseClassified(utf8Yaml, filePath);
        var parseResult = classifiedParseResult.ParseResult;
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

            if (!rule.SupportsDocumentKind(classifiedParseResult.Classification.FinalKind))
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

        return TryGetConfigSuppressionRecord(diagnostic, inlineSuppression.JobScopes, inlineSuppression.Source, normalizedExclusions, normalizedFilePath, out suppressionRecord);
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

        if (!TryFindJobIdForLine(diagnostic.Location.StartLine, inlineSuppression.JobScopes, inlineSuppression.Source, out var jobId))
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
        byte[] source,
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

            if (!TryFindJobIdForLine(diagnostic.Location.StartLine, jobScopes, source, out var jobId))
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

    static bool TryFindJobIdForLine(int line, IReadOnlyList<JobScope> jobScopes, byte[] source, out string jobId)
    {
        for (var i = 0; i < jobScopes.Count; i++)
        {
            var scope = jobScopes[i];
            if (line >= scope.StartLine && line <= scope.EndLine)
            {
                jobId = Encoding.UTF8.GetString(scope.JobIdSlice.AsSpan(source));
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

        var knownJobIdSlices = BuildKnownJobIdSlices(workflow);
        var jobScopes = BuildJobScopes(workflow);

        // UTF-8 byte constants for directive parsing
        ReadOnlySpan<byte> seitonPrefixUtf8 = "seiton:"u8;
        ReadOnlySpan<byte> disableNextLineUtf8 = "disable-next-line"u8;
        ReadOnlySpan<byte> disableFileUtf8 = "disable-file"u8;
        ReadOnlySpan<byte> disableJobUtf8 = "disable-job"u8;

        var nextLineRuleSuppressions = new Dictionary<int, Dictionary<string, SuppressionAnchor>>();
        var fileRuleSuppressions = new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal);
        var jobRuleSuppressions = new Dictionary<string, Dictionary<string, SuppressionAnchor>>(StringComparer.OrdinalIgnoreCase);
        var configurationDiagnostics = new List<Diagnostic>();

        ReadOnlySpan<byte> remaining = utf8Yaml;
        var lineStartOffset = 0;
        var lineNumber = 0;

        while (!remaining.IsEmpty)
        {
            lineNumber++;

            // Find end-of-line ('\n' = 0x0A)
            var newlinePos = remaining.IndexOf((byte)'\n');
            ReadOnlySpan<byte> lineBytes;
            int lineAdvance;
            if (newlinePos >= 0)
            {
                lineBytes = remaining[..newlinePos];
                lineAdvance = newlinePos + 1;
            }
            else
            {
                lineBytes = remaining;
                lineAdvance = remaining.Length;
            }

            // Strip trailing \r (Windows line endings)
            var lineCore = (!lineBytes.IsEmpty && lineBytes[^1] == (byte)'\r')
                ? lineBytes[..^1]
                : lineBytes;

            // Fast path: skip lines without '#'
            var hashPos = lineCore.IndexOf((byte)'#');
            if (hashPos < 0)
            {
                lineStartOffset += lineAdvance;
                remaining = remaining[lineAdvance..];
                continue;
            }

            // Scan past '#' and trim leading whitespace to find 'seiton:'
            var afterHashOffset = hashPos + 1;
            var afterHashBytes = lineCore[afterHashOffset..];
            var leadingWS1 = CountLeadingAsciiWhitespace(afterHashBytes);
            var commentTextOffset = afterHashOffset + leadingWS1;
            var commentText = afterHashBytes[leadingWS1..];

            if (!commentText.StartsWith(seitonPrefixUtf8))
            {
                lineStartOffset += lineAdvance;
                remaining = remaining[lineAdvance..];
                continue;
            }

            // After 'seiton:', trim leading whitespace to find command
            var afterPrefixBytes = commentText[seitonPrefixUtf8.Length..];
            var leadingWS2 = CountLeadingAsciiWhitespace(afterPrefixBytes);
            var commandAndArgsOffset = commentTextOffset + seitonPrefixUtf8.Length + leadingWS2;
            var commandAndArgs = afterPrefixBytes[leadingWS2..];

            if (commandAndArgs.IsEmpty)
            {
                lineStartOffset += lineAdvance;
                remaining = remaining[lineAdvance..];
                continue;
            }

            // Split command from arguments at first space/tab
            var sepIdx = commandAndArgs.IndexOfAny((byte)' ', (byte)'\t');
            ReadOnlySpan<byte> commandBytes;
            int argsOffset;
            ReadOnlySpan<byte> argsBytes;
            if (sepIdx < 0)
            {
                commandBytes = commandAndArgs;
                argsOffset = commandAndArgsOffset + commandAndArgs.Length;
                argsBytes = ReadOnlySpan<byte>.Empty;
            }
            else
            {
                commandBytes = commandAndArgs[..sepIdx];
                var afterSepBytes = commandAndArgs[(sepIdx + 1)..];
                var leadingWS3 = CountLeadingAsciiWhitespace(afterSepBytes);
                argsOffset = commandAndArgsOffset + sepIdx + 1 + leadingWS3;
                var trimmedAfterSep = afterSepBytes[leadingWS3..];
                var trailingWS = CountTrailingAsciiWhitespace(trimmedAfterSep);
                argsBytes = trimmedAfterSep[..(trimmedAfterSep.Length - trailingWS)];
            }

            var commandColumn = commandAndArgsOffset + 1;  // 1-based column of command token
            var commandLen = commandBytes.Length;

            if (commandBytes.SequenceEqual(disableNextLineUtf8))
            {
                if (!argsBytes.IsEmpty)
                {
                    var targetLine = lineNumber + 1;
                    if (!nextLineRuleSuppressions.TryGetValue(targetLine, out var suppressedRuleIds))
                    {
                        suppressedRuleIds = new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal);
                        nextLineRuleSuppressions[targetLine] = suppressedRuleIds;
                    }

                    AddRuleIds(argsBytes, argsOffset, suppressedRuleIds, configurationDiagnostics, filePath, lineStartOffset, lineNumber);
                }

                lineStartOffset += lineAdvance;
                remaining = remaining[lineAdvance..];
                continue;
            }

            if (commandBytes.SequenceEqual(disableFileUtf8))
            {
                if (!argsBytes.IsEmpty)
                {
                    AddRuleIds(argsBytes, argsOffset, fileRuleSuppressions, configurationDiagnostics, filePath, lineStartOffset, lineNumber);
                }

                lineStartOffset += lineAdvance;
                remaining = remaining[lineAdvance..];
                continue;
            }

            if (commandBytes.SequenceEqual(disableJobUtf8))
            {
                var jobSep = argsBytes.IndexOfAny((byte)' ', (byte)'\t');
                if (jobSep <= 0)
                {
                    configurationDiagnostics.Add(BuildInlineDirectiveError(
                        "disable-job requires <job-id> and <rule-id list>",
                        filePath,
                        lineStartOffset,
                        lineNumber,
                        commandColumn,
                        commandLen));
                    lineStartOffset += lineAdvance;
                    remaining = remaining[lineAdvance..];
                    continue;
                }

                var jobIdBytes = argsBytes[..jobSep];
                var jobIdColumn = argsOffset + 1;  // 1-based column of job ID
                var afterJobBytes = argsBytes[(jobSep + 1)..];
                var leadingWS4 = CountLeadingAsciiWhitespace(afterJobBytes);
                var ruleIdListOffset = argsOffset + jobSep + 1 + leadingWS4;
                var ruleIdListTrimmed = afterJobBytes[leadingWS4..];
                var trailingWS2 = CountTrailingAsciiWhitespace(ruleIdListTrimmed);
                var ruleIdListBytes = ruleIdListTrimmed[..(ruleIdListTrimmed.Length - trailingWS2)];

                if (ruleIdListBytes.IsEmpty)
                {
                    configurationDiagnostics.Add(BuildInlineDirectiveError(
                        "disable-job requires at least one rule-id",
                        filePath,
                        lineStartOffset,
                        lineNumber,
                        commandColumn,
                        commandLen));
                    lineStartOffset += lineAdvance;
                    remaining = remaining[lineAdvance..];
                    continue;
                }

                // Check if job ID is known via byte comparison (no string decode)
                var knownJob = false;
                for (var ki = 0; ki < knownJobIdSlices.Length; ki++)
                {
                    if (knownJobIdSlices[ki].AsSpan(utf8Yaml).SequenceEqual(jobIdBytes))
                    {
                        knownJob = true;
                        break;
                    }
                }

                if (!knownJob)
                {
                    var jobIdString = Encoding.UTF8.GetString(jobIdBytes);
                    configurationDiagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        $"unknown job-id '{jobIdString}' in inline suppression directive",
                        new TextRange(lineStartOffset + jobIdColumn - 1, jobIdBytes.Length, lineNumber, jobIdColumn, lineNumber, jobIdColumn + jobIdBytes.Length),
                        FilePath: filePath));
                    lineStartOffset += lineAdvance;
                    remaining = remaining[lineAdvance..];
                    continue;
                }

                var jobIdKey = Encoding.UTF8.GetString(jobIdBytes);
                if (!jobRuleSuppressions.TryGetValue(jobIdKey, out var jobSuppressedRuleIds))
                {
                    jobSuppressedRuleIds = new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal);
                    jobRuleSuppressions[jobIdKey] = jobSuppressedRuleIds;
                }

                AddRuleIds(ruleIdListBytes, ruleIdListOffset, jobSuppressedRuleIds, configurationDiagnostics, filePath, lineStartOffset, lineNumber);
                lineStartOffset += lineAdvance;
                remaining = remaining[lineAdvance..];
                continue;
            }

            // Unknown command
            configurationDiagnostics.Add(BuildInlineDirectiveError(
                $"unknown inline suppression command '{Encoding.UTF8.GetString(commandBytes)}'",
                filePath,
                lineStartOffset,
                lineNumber,
                commandColumn,
                commandLen));

            lineStartOffset += lineAdvance;
            remaining = remaining[lineAdvance..];
        }

        return new InlineSuppression(
            nextLineRuleSuppressions,
            fileRuleSuppressions,
            jobRuleSuppressions,
            jobScopes,
            utf8Yaml,
            configurationDiagnostics.ToArray());
    }

    static Diagnostic BuildInlineDirectiveError(string message, string filePath, int lineStartOffset, int lineNumber, int tokenColumn, int tokenLength)
    {
        var tokenStart = lineStartOffset + tokenColumn - 1;
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

    static Utf8Slice[] BuildKnownJobIdSlices(Parsing.Ast.Workflow workflow)
    {
        var count = 0;
        foreach (var pair in workflow.Jobs)
        {
            if (!pair.Value.Id.Value.IsEmpty)
                count++;
        }

        if (count == 0)
            return [];

        var result = new Utf8Slice[count];
        var i = 0;
        foreach (var pair in workflow.Jobs)
        {
            var slice = pair.Value.Id.Value;
            if (!slice.IsEmpty)
                result[i++] = slice;
        }

        return result;
    }

    static IReadOnlyList<JobScope> BuildJobScopes(Parsing.Ast.Workflow workflow)
    {
        var scopes = new List<JobScope>(workflow.Jobs.Count);
        foreach (var pair in workflow.Jobs)
        {
            var slice = pair.Value.Id.Value;
            if (slice.IsEmpty)
            {
                continue;
            }

            var range = pair.Value.Range;
            if (range.StartLine <= 0 || range.EndLine <= 0)
            {
                continue;
            }

            scopes.Add(new JobScope(slice, range.StartLine, range.EndLine));
        }

        return scopes;
    }

    static void AddRuleIds(
        ReadOnlySpan<byte> ruleIdListBytes,
        int argsLineOffset,
        Dictionary<string, SuppressionAnchor> target,
        List<Diagnostic> configurationDiagnostics,
        string filePath,
        int lineStartOffset,
        int lineNumber)
    {
        var remaining = ruleIdListBytes;
        var currentOffset = argsLineOffset;
        while (true)
        {
            var commaIdx = remaining.IndexOf((byte)',');
            ReadOnlySpan<byte> tokenBytes;
            bool hasMore;
            if (commaIdx >= 0)
            {
                tokenBytes = remaining[..commaIdx];
                hasMore = true;
            }
            else
            {
                tokenBytes = remaining;
                hasMore = false;
            }

            // Trim leading/trailing ASCII whitespace from token
            var leadingWS = CountLeadingAsciiWhitespace(tokenBytes);
            var strippedLeading = tokenBytes[leadingWS..];
            var trailingWS = CountTrailingAsciiWhitespace(strippedLeading);
            var trimmedToken = strippedLeading[..(strippedLeading.Length - trailingWS)];

            if (!trimmedToken.IsEmpty)
            {
                var tokenByteOffset = currentOffset + leadingWS;
                var tokenColumn = tokenByteOffset + 1;          // 1-based column
                var tokenAbsStart = lineStartOffset + tokenByteOffset;
                var ruleIdToken = Encoding.UTF8.GetString(trimmedToken);

                if (RuleCatalog.TryResolveRuleId(ruleIdToken, out var internalRuleId))
                {
                    if (RuleCatalog.IsNonDisableable(internalRuleId))
                    {
                        configurationDiagnostics.Add(new Diagnostic(
                            DiagnosticSeverity.Error,
                            $"rule '{internalRuleId}' is non-disableable",
                            new TextRange(tokenAbsStart, trimmedToken.Length, lineNumber, tokenColumn, lineNumber, tokenColumn + trimmedToken.Length),
                            FilePath: filePath));
                    }
                    else
                    {
                        target[internalRuleId] = new SuppressionAnchor(lineNumber, tokenColumn);
                    }
                }
                else
                {
                    configurationDiagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        BuildUnknownRuleIdMessage(ruleIdToken),
                        new TextRange(tokenAbsStart, trimmedToken.Length, lineNumber, tokenColumn, lineNumber, tokenColumn + trimmedToken.Length),
                        FilePath: filePath));
                }
            }

            if (!hasMore)
                break;

            currentOffset += commaIdx + 1;
            remaining = remaining[(commaIdx + 1)..];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAsciiWhitespace(byte b) => b == (byte)' ' || b == (byte)'\t';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountLeadingAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        var i = 0;
        while (i < span.Length && IsAsciiWhitespace(span[i]))
            i++;
        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountTrailingAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        var i = span.Length;
        while (i > 0 && IsAsciiWhitespace(span[i - 1]))
            i--;
        return span.Length - i;
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
        byte[] Source,
        Diagnostic[] ConfigurationDiagnostics)
    {
        public static InlineSuppression Empty { get; } = new(
            new Dictionary<int, Dictionary<string, SuppressionAnchor>>(),
            new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal),
            new Dictionary<string, Dictionary<string, SuppressionAnchor>>(StringComparer.Ordinal),
            [],
            [],
            []);
    }

    readonly record struct JobScope(Utf8Slice JobIdSlice, int StartLine, int EndLine);

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
