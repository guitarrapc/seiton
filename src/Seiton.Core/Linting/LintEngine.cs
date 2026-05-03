using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;
using System.Runtime.CompilerServices;
using System.Text;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting;

/// <summary>
/// Core lint engine that parses a workflow/action YAML file, runs all enabled rules via
/// <see cref="WorkflowVisitor"/> traversal, and returns aggregated <see cref="LintResult"/> diagnostics.
/// </summary>
public sealed class LintEngine
{
    private static readonly Workflow EmptyWorkflowForSuppression = new() { Range = default };

    private readonly List<IRule> rules = [];
    private readonly List<IOnlineRule> _onlineRules = [];
    private readonly List<Diagnostic> _diagnostics = new(16);
    private readonly WorkflowVisitor _visitor = new();
    private readonly List<IRule> _activeRules = new(16);
    private readonly List<IOnlineRule> _activeOnlineRules = new(4);
    private readonly List<Diagnostic> _ruleDiagnostics = new(64);
    private readonly HashSet<DiagnosticIdentity> _seen = new();
    private readonly Dictionary<string, int> _suppressedByRule = new(StringComparer.Ordinal);
    private readonly List<SuppressionRecord> _suppressionRecords = new();
    private readonly LintConfig _effectiveConfig = new();

    // NormalizeRules reusable collections
    private readonly Dictionary<string, RuleConfig> _normalizedRulesDict = new(StringComparer.Ordinal);
    private readonly List<Diagnostic> _ruleNormDiagnostics = new();

    // ParseInlineSuppression reusable collections
    private readonly Dictionary<int, Dictionary<string, SuppressionAnchor>> _nextLineRuleSuppressions = new();
    private readonly Dictionary<string, SuppressionAnchor> _fileRuleSuppressions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, SuppressionAnchor>> _jobRuleSuppressions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Diagnostic> _suppressionDiagnostics = new();
    private readonly List<JobScope> _jobScopes = new();

    // S-6: Reusable buffer for BuildKnownJobIdSlices (avoids per-call allocation)
    private Utf8Slice[] _knownJobIdSlices = new Utf8Slice[8];

    // A-8: NormalizeExclusions reusable collections
    private readonly List<NormalizedExclusion> _normalizedExclusions = new(4);
    private readonly List<Diagnostic> _exclusionDiagnostics = new();

    // Arena lifecycle: two-slot rotation to honour the "most recent + preceding" validity guarantee.
    // _arenaToDispose holds N-2 (disposed on next Check), _previousArena holds N-1 (still valid).
    private AstArena? _arenaToDispose;
    private AstArena? _previousArena;

    /// <summary>
    /// Online rules that were activated during the most recent <see cref="Check"/> call.
    /// Pass to <see cref="OnlineAudit.OnlineAuditEngine.AuditAsync"/> for post-traversal async resolution.
    /// </summary>
    public IReadOnlyList<IOnlineRule> ActiveOnlineRules => _activeOnlineRules;

    /// <summary>Creates a new <see cref="LintEngine"/> with the default rule set from <see cref="RuleCatalog"/>.</summary>
    public LintEngine()
    {
        rules.AddRange(RuleCatalog.CreateDefaultRules());
        _onlineRules.AddRange(RuleCatalog.CreateOnlineRules());
    }

    /// <summary>Creates a new <see cref="LintEngine"/> with the specified <paramref name="rules"/>.</summary>
    public LintEngine(IEnumerable<IRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (var rule in rules)
        {
            AddRule(rule);
        }
    }

    /// <summary>Adds a rule to the engine. Online rules are registered separately for async resolution.</summary>
    public void AddRule(IRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule is IOnlineRule onlineRule)
        {
            _onlineRules.Add(onlineRule);
        }
        else
        {
            rules.Add(rule);
        }
    }

    /// <summary>Parses and lints the given YAML with no explicit configuration.</summary>
    /// <inheritdoc cref="Check(byte[], string, LintConfig?)"/>
    public LintResult Check(byte[] utf8Yaml, string filePath)
    {
        return Check(utf8Yaml, filePath, config: null);
    }

    /// <summary>Parses and lints the given YAML, applying the optional <paramref name="config"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Result lifetime:</b> The returned <see cref="LintResult"/> shares backing arrays with the engine
    /// via a two-buffer swap pattern. Only the most recent result and the immediately preceding one are
    /// guaranteed to remain valid. Callers must not retain a <see cref="LintResult"/> across more than one
    /// subsequent <see cref="Check"/> call on the same <see cref="LintEngine"/> instance.
    /// Use <see cref="LintResult.CopyDiagnostics"/> to obtain a caller-owned snapshot that is safe to retain.
    /// </para>
    /// </remarks>
    public LintResult Check(byte[] utf8Yaml, string filePath, LintConfig? config)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        // Arena lifecycle: dispose N-2, rotate N-1 → N-2, then parse (Rent reuses from cache).
        _arenaToDispose?.Dispose();
        _arenaToDispose = _previousArena;

        var classifiedParseResult = WorkflowParser.ParseClassified(utf8Yaml, filePath);
        var parseResult = classifiedParseResult.ParseResult;

        _previousArena = parseResult.Arena;

        return CheckCore(utf8Yaml, filePath, config, parseResult, classifiedParseResult.Classification.FinalKind);
    }

    /// <summary>
    /// Lints a pre-parsed <see cref="ParseResult"/> without re-parsing.
    /// Used by Playground incremental parsing (D-5b) where parsing is done externally.
    /// Assumes the document is a workflow (DocumentKind.Workflow).
    /// </summary>
    internal LintResult CheckWithParseResult(byte[] utf8Yaml, string filePath, LintConfig? config, ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        return CheckCore(utf8Yaml, filePath, config, parseResult, DocumentKind.Workflow);
    }

    /// <summary>
    /// Lints a pre-parsed <see cref="ParseResult"/> with optional job skipping (D-5d).
    /// When <paramref name="skipJobs"/>[i] is true, lint rules are not run on that job
    /// (its diagnostics are expected to be supplied from a cache by the caller).
    /// </summary>
    internal LintResult CheckWithParseResult(byte[] utf8Yaml, string filePath, LintConfig? config, ParseResult parseResult, bool[]? skipJobs)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        return CheckCore(utf8Yaml, filePath, config, parseResult, DocumentKind.Workflow, skipJobs);
    }

    private LintResult CheckCore(byte[] utf8Yaml, string filePath, LintConfig? config, ParseResult parseResult, DocumentKind documentKind, bool[]? skipJobs = null)
    {
        if (parseResult.HasFatalError || (parseResult.Workflow is null && parseResult.ActionMetadata is null))
        {
            return new LintResult(parseResult, parseResult.Diagnostics)
            {
                SuppressionSummary = SuppressionSummary.Empty,
            };
        }

        _diagnostics.Clear();
        _seen.Clear();
        for (var i = 0; i < parseResult.Diagnostics.Length; i++)
        {
            if (_seen.Add(new DiagnosticIdentity(parseResult.Diagnostics[i])))
            {
                _diagnostics.Add(parseResult.Diagnostics[i]);
            }
        }

        var normalizedRules = NormalizeRules(config?.Rules, filePath);
        _diagnostics.AddRange(normalizedRules.ConfigurationDiagnostics);

        var workflowForSuppression = parseResult.Workflow ?? EmptyWorkflowForSuppression;
        var inlineSuppression = ParseInlineSuppression(utf8Yaml, filePath, workflowForSuppression, parseResult.Arena!);
        _diagnostics.AddRange(inlineSuppression.ConfigurationDiagnostics);

        var normalizedExclusions = NormalizeExclusions(config?.Exclusions, filePath, workflowForSuppression, utf8Yaml, parseResult.Arena!);
        _diagnostics.AddRange(normalizedExclusions.ConfigurationDiagnostics);

        if (rules.Count == 0 && _onlineRules.Count == 0)
        {
            return BuildLintResult(parseResult);
        }

        _visitor.Reset();
        _effectiveConfig.PrepareForRun(
            utf8Yaml,
            parseResult.Arena,
            filePath,
            normalizedRules.Rules,
            config?.Fix,
            config?.Network,
            config?.Output);
        var effectiveConfig = _effectiveConfig;

        _activeRules.Clear();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (!IsRuleEnabled(rule.Id.ToId(), effectiveConfig.Rules))
            {
                continue;
            }

            if (!rule.SupportsDocumentKind(documentKind))
            {
                continue;
            }

            rule.SetConfig(effectiveConfig);
            _visitor.AddPass(rule);
            _activeRules.Add(rule);
        }

        // Activate online rules for visitor traversal (target collection)
        _activeOnlineRules.Clear();
        for (var i = 0; i < _onlineRules.Count; i++)
        {
            var onlineRule = _onlineRules[i];
            if (!IsRuleEnabled(onlineRule.Id.ToId(), effectiveConfig.Rules))
            {
                continue;
            }

            if (!onlineRule.SupportsDocumentKind(documentKind))
            {
                continue;
            }

            onlineRule.SetConfig(effectiveConfig);
            _visitor.AddPass(onlineRule);
            _activeOnlineRules.Add(onlineRule);
        }

        if (_activeRules.Count == 0 && _activeOnlineRules.Count == 0)
        {
            return BuildLintResult(parseResult);
        }

        if (parseResult.Workflow is not null)
        {
            _visitor.Visit(parseResult.Workflow, skipJobs);
        }
        else if (parseResult.ActionMetadata is not null)
        {
            _visitor.VisitActionMetadata(parseResult.ActionMetadata);
        }

        _ruleDiagnostics.Clear();
        for (var i = 0; i < _activeRules.Count; i++)
        {
            var currentRuleDiagnostics = _activeRules[i].GetDiagnostics();
            for (var j = 0; j < currentRuleDiagnostics.Count; j++)
            {
                var current = currentRuleDiagnostics[j];
                if (TryGetSeverityOverride(current.RuleId, effectiveConfig.Rules, out var severityOverride))
                {
                    current = current with { Severity = severityOverride };
                }

                _ruleDiagnostics.Add(current);
            }
        }

        var sortOrder = effectiveConfig.Output.SortOrder;
        if (sortOrder == DiagnosticSortOrder.Rule)
        {
            _ruleDiagnostics.Sort(static (x, y) => CompareDiagnosticsByRulePriority(x, y));
        }
        else
        {
            _ruleDiagnostics.Sort(static (x, y) => CompareDiagnosticsByLocation(x, y));
        }

        _seen.Clear();

        // Seed _seen with parser diagnostic identities so lint rules that duplicate
        // the same check (e.g. JobStructureRule, ReusableWorkflowRule) are suppressed.
        // Track indices for replacement: when a lint rule produces the same diagnostic,
        // we replace the parser version (RuleId = null) with the lint version (has RuleId)
        // so that rule-based suppression and filtering still work.
        for (var i = 0; i < _diagnostics.Count; i++)
        {
            _seen.Add(new DiagnosticIdentity(_diagnostics[i]));
        }

        _suppressedByRule.Clear();
        _suppressionRecords.Clear();
        for (var i = 0; i < _ruleDiagnostics.Count; i++)
        {
            var current = _ruleDiagnostics[i];
            var identity = new DiagnosticIdentity(current);
            if (!_seen.Add(identity))
            {
                // Lint diagnostic duplicates a parser diagnostic — replace the parser entry
                // so the RuleId is preserved for suppression and diagnostic attribution.
                for (var j = 0; j < _diagnostics.Count; j++)
                {
                    if (_diagnostics[j].RuleId is null && new DiagnosticIdentity(_diagnostics[j]).Equals(identity))
                    {
                        _diagnostics[j] = current;
                        break;
                    }
                }

                continue;
            }

            if (TryGetSuppressionRecord(current, inlineSuppression, normalizedExclusions.Exclusions, normalizedExclusions.NormalizedFilePath, out var suppressionRecord))
            {
                _suppressionRecords.Add(suppressionRecord);
                if (!_suppressedByRule.TryGetValue(suppressionRecord.RuleId, out var currentCount))
                {
                    _suppressedByRule[suppressionRecord.RuleId] = 1;
                }
                else
                {
                    _suppressedByRule[suppressionRecord.RuleId] = currentCount + 1;
                }

                continue;
            }

            _diagnostics.Add(current);
        }

        // Global sort: parser diagnostics were prepended before rule diagnostics;
        // re-sort the entire list so all diagnostics are in the configured order.
        if (sortOrder == DiagnosticSortOrder.Rule)
        {
            _diagnostics.Sort(static (x, y) => CompareDiagnosticsByRulePriority(x, y));
        }
        else
        {
            _diagnostics.Sort(static (x, y) => CompareDiagnosticsByLocation(x, y));
        }

        if (config?.SkipSuppressionSummary == true)
        {
            return BuildLintResult(parseResult);
        }

        return BuildLintResultWithSuppression(parseResult);
    }

    /// <summary>
    /// Copies <c>_diagnostics</c> into an exact-sized array using a two-buffer swap pattern.
    /// When the previous result's array (now in <c>_resultDiagnosticsSwap</c>) has the right length,
    /// it is reused with zero allocation. Otherwise a new array is allocated.
    /// </summary>
    private LintResult BuildLintResult(ParseResult parseResult)
    {
        var count = _diagnostics.Count;
        var buffer = new PooledBuffer<Diagnostic>(count > 0 ? count : 4);
        for (var i = 0; i < count; i++)
        {
            buffer.Add(_diagnostics[i]);
        }

        var (diagArray, diagCount) = buffer.DetachArray();
        buffer.Dispose();
        parseResult.Arena?.RegisterLintDiagnosticsBuffer(diagArray);

        return new LintResult(parseResult, new DiagnosticList(diagArray, diagCount))
        {
            SuppressionSummary = SuppressionSummary.Empty,
        };
    }

    /// <summary>
    /// Builds a <see cref="LintResult"/> with suppression summary using PooledBuffer + DetachArray.
    /// </summary>
    private LintResult BuildLintResultWithSuppression(ParseResult parseResult)
    {
        var count = _diagnostics.Count;
        var buffer = new PooledBuffer<Diagnostic>(count > 0 ? count : 4);
        for (var i = 0; i < count; i++)
        {
            buffer.Add(_diagnostics[i]);
        }

        var (diagArray, diagCount) = buffer.DetachArray();
        buffer.Dispose();
        parseResult.Arena?.RegisterLintDiagnosticsBuffer(diagArray);

        // Create caller-owned snapshots for suppression summary.
        // Suppression data uses snapshot semantics so callers can safely
        // retain SuppressionSummary across subsequent Check() calls.
        var suppressionCount = _suppressionRecords.Count;
        var suppressionRecordsSnapshot = new SuppressionRecord[suppressionCount];
        for (var i = 0; i < suppressionCount; i++)
        {
            suppressionRecordsSnapshot[i] = _suppressionRecords[i];
        }

        var suppressedByRuleSnapshot = new Dictionary<string, int>(_suppressedByRule.Count, StringComparer.Ordinal);
        foreach (var pair in _suppressedByRule)
        {
            suppressedByRuleSnapshot[pair.Key] = pair.Value;
        }

        return new LintResult(parseResult, new DiagnosticList(diagArray, diagCount))
        {
            SuppressionSummary = new SuppressionSummary(suppressionCount, suppressedByRuleSnapshot, suppressionRecordsSnapshot),
        };
    }

    private static bool IsRuleEnabled(string? ruleId, IReadOnlyDictionary<string, RuleConfig>? rules)
    {
        if (!TryGetRuleConfig(ruleId, rules, out var ruleConfig))
        {
            // No config found: local rules are enabled by default, opt-in rules are disabled.
            return !RuleCatalog.IsOptIn(ruleId);
        }

        return ruleConfig!.Enabled;
    }

    private static bool TryGetSeverityOverride(string? ruleId, IReadOnlyDictionary<string, RuleConfig>? rules, out DiagnosticSeverity severity)
    {
        if (TryGetRuleConfig(ruleId, rules, out var ruleConfig) && ruleConfig?.Severity is not null)
        {
            severity = ruleConfig.Severity.Value;
            return true;
        }

        severity = default;
        return false;
    }

    private static bool TryGetRuleConfig(string? ruleId, IReadOnlyDictionary<string, RuleConfig>? rules, out RuleConfig? config)
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

        return rules.TryGetValue(resolvedRuleId.ToId(), out config);
    }

    private static bool TryGetSuppressionRecord(
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

    private static bool TryGetInlineSuppressionRecord(Diagnostic diagnostic, InlineSuppression inlineSuppression, out SuppressionRecord suppressionRecord)
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

    private static bool TryGetConfigSuppressionRecord(
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
            if (!GlobMatch(exclusion.File, normalizedFilePath))
            {
                continue;
            }

            // Rules == null means all rules are suppressed
            if (exclusion.Rules is not null && !exclusion.Rules.Contains(diagnostic.RuleId))
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

    private static bool TryFindJobIdForLine(int line, IReadOnlyList<JobScope> jobScopes, byte[] source, out string jobId)
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

    private InlineSuppression ParseInlineSuppression(byte[] utf8Yaml, string filePath, Parsing.Ast.Workflow workflow, AstArena arena)
    {
        if (utf8Yaml.Length == 0)
        {
            return InlineSuppression.Empty;
        }

        var knownJobIdSlices = BuildKnownJobIdSlices(workflow, arena);
        BuildJobScopes(workflow, arena);

        // UTF-8 byte constants for directive parsing
        ReadOnlySpan<byte> seitonPrefixUtf8 = "seiton:"u8;
        ReadOnlySpan<byte> disableNextLineUtf8 = "disable-next-line"u8;
        ReadOnlySpan<byte> disableFileUtf8 = "disable-file"u8;
        ReadOnlySpan<byte> disableJobUtf8 = "disable-job"u8;

        // Clear reusable collections; inner dicts of nextLine/job are discarded on Clear
        _nextLineRuleSuppressions.Clear();
        _fileRuleSuppressions.Clear();
        _jobRuleSuppressions.Clear();
        _suppressionDiagnostics.Clear();

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
                    if (!_nextLineRuleSuppressions.TryGetValue(targetLine, out var suppressedRuleIds))
                    {
                        suppressedRuleIds = new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal);
                        _nextLineRuleSuppressions[targetLine] = suppressedRuleIds;
                    }

                    AddRuleIds(argsBytes, argsOffset, suppressedRuleIds, _suppressionDiagnostics, filePath, lineStartOffset, lineNumber);
                }

                lineStartOffset += lineAdvance;
                remaining = remaining[lineAdvance..];
                continue;
            }

            if (commandBytes.SequenceEqual(disableFileUtf8))
            {
                if (!argsBytes.IsEmpty)
                {
                    AddRuleIds(argsBytes, argsOffset, _fileRuleSuppressions, _suppressionDiagnostics, filePath, lineStartOffset, lineNumber);
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
                    _suppressionDiagnostics.Add(BuildInlineDirectiveError(
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
                    _suppressionDiagnostics.Add(BuildInlineDirectiveError(
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
                    _suppressionDiagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        $"unknown job-id '{jobIdString}' in inline suppression directive",
                        new TextRange(lineStartOffset + jobIdColumn - 1, jobIdBytes.Length, lineNumber, jobIdColumn, lineNumber, jobIdColumn + jobIdBytes.Length),
                        FilePath: filePath));
                    lineStartOffset += lineAdvance;
                    remaining = remaining[lineAdvance..];
                    continue;
                }

                var jobIdKey = Encoding.UTF8.GetString(jobIdBytes);
                if (!_jobRuleSuppressions.TryGetValue(jobIdKey, out var jobSuppressedRuleIds))
                {
                    jobSuppressedRuleIds = new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal);
                    _jobRuleSuppressions[jobIdKey] = jobSuppressedRuleIds;
                }

                AddRuleIds(ruleIdListBytes, ruleIdListOffset, jobSuppressedRuleIds, _suppressionDiagnostics, filePath, lineStartOffset, lineNumber);
                lineStartOffset += lineAdvance;
                remaining = remaining[lineAdvance..];
                continue;
            }

            // Unknown command
            _suppressionDiagnostics.Add(BuildInlineDirectiveError(
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
            _nextLineRuleSuppressions,
            _fileRuleSuppressions,
            _jobRuleSuppressions,
            _jobScopes,
            utf8Yaml,
            _suppressionDiagnostics);
    }

    private static Diagnostic BuildInlineDirectiveError(string message, string filePath, int lineStartOffset, int lineNumber, int tokenColumn, int tokenLength)
    {
        var tokenStart = lineStartOffset + tokenColumn - 1;
        return new Diagnostic(
            DiagnosticSeverity.Error,
            message,
            new TextRange(tokenStart, tokenLength, lineNumber, tokenColumn, lineNumber, tokenColumn + tokenLength),
            FilePath: filePath);
    }

    private ReadOnlySpan<Utf8Slice> BuildKnownJobIdSlices(Parsing.Ast.Workflow workflow, AstArena arena)
    {
        var count = 0;
        foreach (var pair in workflow.Jobs)
        {
            if (!arena.GetStringSlice(pair.Value.Id).IsEmpty)
                count++;
        }

        if (count == 0)
            return [];

        if (_knownJobIdSlices.Length < count)
        {
            _knownJobIdSlices = new Utf8Slice[count];
        }

        var i = 0;
        foreach (var pair in workflow.Jobs)
        {
            var slice = arena.GetStringSlice(pair.Value.Id);
            if (!slice.IsEmpty)
                _knownJobIdSlices[i++] = slice;
        }

        return _knownJobIdSlices.AsSpan(0, count);
    }

    private void BuildJobScopes(Parsing.Ast.Workflow workflow, AstArena arena)
    {
        _jobScopes.Clear();
        foreach (var pair in workflow.Jobs)
        {
            var slice = arena.GetStringSlice(pair.Value.Id);
            if (slice.IsEmpty)
            {
                continue;
            }

            var range = pair.Value.Range;
            if (range.StartLine <= 0 || range.EndLine <= 0)
            {
                continue;
            }

            _jobScopes.Add(new JobScope(slice, range.StartLine, range.EndLine));
        }
    }

    private static void AddRuleIds(
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
                    var internalRuleIdString = internalRuleId.ToId();
                    if (RuleCatalog.IsNonDisableable(internalRuleId))
                    {
                        configurationDiagnostics.Add(new Diagnostic(
                            DiagnosticSeverity.Error,
                            $"rule '{internalRuleIdString}' is non-disableable",
                            new TextRange(tokenAbsStart, trimmedToken.Length, lineNumber, tokenColumn, lineNumber, tokenColumn + trimmedToken.Length),
                            FilePath: filePath));
                    }
                    else
                    {
                        target[internalRuleIdString] = new SuppressionAnchor(lineNumber, tokenColumn);
                    }
                }
                else
                {
                    configurationDiagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        RuleNormalizer.BuildUnknownRuleIdMessage(ruleIdToken),
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
    private static bool ContainsJobIdOrdinalIgnoreCase(ReadOnlySpan<Utf8Slice> slices, byte[] source, string configJobId)
    {
        for (var k = 0; k < slices.Length; k++)
        {
            if (MatchesJobIdOrdinalIgnoreCase(slices[k].AsSpan(source), configJobId))
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchesJobIdOrdinalIgnoreCase(ReadOnlySpan<byte> utf8, string str)
    {
        // GitHub Actions job IDs are ASCII identifiers: byte length equals character length
        if (utf8.Length != str.Length)
            return false;
        for (var i = 0; i < utf8.Length; i++)
        {
            var b = utf8[i];
            var c = (byte)str[i];
            if (b == c)
                continue;
            // ASCII case folding: A-Z (0x41-0x5A) <-> a-z (0x61-0x7A)
            var bLower = (byte)(b | 0x20);
            if (bLower >= (byte)'a' && bLower <= (byte)'z' && bLower == (byte)(c | 0x20))
                continue;
            return false;
        }
        return true;
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

    private RulesNormalization NormalizeRules(IReadOnlyDictionary<string, RuleConfig>? rules, string filePath)
    {
        if (rules is null || rules.Count == 0)
        {
            return RulesNormalization.Empty;
        }

        _normalizedRulesDict.Clear();
        _ruleNormDiagnostics.Clear();
        RuleNormalizer.NormalizeRuleEntries(rules, filePath, _ruleNormDiagnostics, _normalizedRulesDict);
        return new RulesNormalization(_normalizedRulesDict, _ruleNormDiagnostics);
    }

    private ExclusionsNormalization NormalizeExclusions(IReadOnlyList<LintExclusion>? exclusions, string filePath, Parsing.Ast.Workflow workflow, byte[] utf8Yaml, AstArena arena)
    {
        var normalizedFilePath = NormalizePath(filePath);
        if (exclusions is null || exclusions.Count == 0)
        {
            return new ExclusionsNormalization([], normalizedFilePath, []);
        }

        var knownJobIdSlices = BuildKnownJobIdSlices(workflow, arena);
        _normalizedExclusions.Clear();
        _exclusionDiagnostics.Clear();

        for (var i = 0; i < exclusions.Count; i++)
        {
            var exclusion = exclusions[i];
            if (string.IsNullOrWhiteSpace(exclusion.File))
            {
                _exclusionDiagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "exclusion file pattern must not be empty",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: filePath));
                continue;
            }

            IReadOnlySet<string>? normalizedRuleIds;
            if (exclusion.Rules is null)
            {
                // rules omitted → all rules
                normalizedRuleIds = null;
            }
            else
            {
                var ruleIds = new HashSet<string>(StringComparer.Ordinal);
                ExclusionNormalizer.CollectResolvedExclusionRules(exclusion.Rules, filePath, _exclusionDiagnostics, ruleIds);

                if (ruleIds.Count == 0)
                {
                    continue;
                }

                normalizedRuleIds = ruleIds;
            }

            if (exclusion.Jobs is not null)
            {
                for (var j = 0; j < exclusion.Jobs.Count; j++)
                {
                    var jobId = exclusion.Jobs[j];
                    if (!string.IsNullOrEmpty(jobId) && !ContainsJobIdOrdinalIgnoreCase(knownJobIdSlices, utf8Yaml, jobId))
                    {
                        _exclusionDiagnostics.Add(new Diagnostic(
                            DiagnosticSeverity.Error,
                            $"unknown job-id '{jobId}' in exclusion configuration",
                            new TextRange(0, jobId.Length, 1, 1, 1, 1 + jobId.Length),
                            FilePath: filePath));
                    }
                }
            }

            _normalizedExclusions.Add(new NormalizedExclusion(NormalizePath(exclusion.File), normalizedRuleIds, exclusion.Jobs));
        }

        return new ExclusionsNormalization(
            _normalizedExclusions.Count > 0 ? _normalizedExclusions.ToArray() : [],
            normalizedFilePath,
            _exclusionDiagnostics.Count > 0 ? _exclusionDiagnostics.ToArray() : []);
    }
    private static int CompareDiagnosticsByRulePriority(Diagnostic x, Diagnostic y)
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

    private static int CompareDiagnosticsByLocation(Diagnostic x, Diagnostic y)
    {
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

        var byRuleId = string.CompareOrdinal(x.RuleId, y.RuleId);
        if (byRuleId != 0)
        {
            return byRuleId;
        }

        return string.CompareOrdinal(x.Message, y.Message);
    }

    /// <summary>
    /// Identity used for diagnostic deduplication.
    /// Matches on severity + normalized message + line only (ignoring column / byte offset) so that
    /// parser diagnostics (reported at expression-internal positions) and lint diagnostics
    /// (reported at YAML key positions) on the same line with the same message are treated
    /// as duplicates.
    /// The message is normalized by stripping the leading <c>jobs.'…'.steps[N] </c> prefix
    /// so that alias-expanded steps sharing the same source position are deduplicated even
    /// though each carries a distinct step index.
    /// Zero-alloc: stores the original message with a suffix start index instead of allocating
    /// a Substring. Equality and hash operate on the suffix span.
    /// </summary>
    private readonly struct DiagnosticIdentity : IEquatable<DiagnosticIdentity>
    {
        private readonly DiagnosticSeverity _severity;
        private readonly string _message;
        private readonly int _suffixStart;
        private readonly int _startLine;

        public DiagnosticIdentity(Diagnostic diagnostic)
        {
            _severity = diagnostic.Severity;
            _message = diagnostic.Message;
            _suffixStart = FindSuffixStart(diagnostic.Message);
            _startLine = diagnostic.Location.StartLine;
        }

        /// <summary>
        /// Finds the start index of the message suffix after stripping the leading
        /// <c>jobs.'…'.steps[N] </c> or <c>steps[N] </c> prefix.
        /// Returns 0 if neither pattern is matched.
        /// </summary>
        private static int FindSuffixStart(string message)
        {
            // Pattern 1: jobs.'<id>'.steps[<n>] <rest>
            if (message.StartsWith("jobs.'", StringComparison.Ordinal))
            {
                var dotSteps = message.IndexOf("'.steps[", 6, StringComparison.Ordinal);
                if (dotSteps >= 0)
                {
                    var bracketClose = message.IndexOf("] ", dotSteps + 8, StringComparison.Ordinal);
                    if (bracketClose >= 0)
                    {
                        return bracketClose + 2;
                    }
                }

                return 0;
            }

            // Pattern 2: steps[<n>] <rest> (action metadata composite steps)
            if (message.StartsWith("steps[", StringComparison.Ordinal))
            {
                var bracketClose = message.IndexOf("] ", 6, StringComparison.Ordinal);
                if (bracketClose >= 0)
                {
                    return bracketClose + 2;
                }
            }

            return 0;
        }

        public bool Equals(DiagnosticIdentity other)
        {
            if (_severity != other._severity || _startLine != other._startLine)
            {
                return false;
            }

            var left = _message.AsSpan(_suffixStart);
            var right = other._message.AsSpan(other._suffixStart);
            return left.SequenceEqual(right);
        }

        public override bool Equals(object? obj) => obj is DiagnosticIdentity other && Equals(other);

        public override int GetHashCode()
        {
            var suffix = _message.AsSpan(_suffixStart);
            var hash = new HashCode();
            hash.Add((int)_severity);
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(suffix));
            hash.Add(_startLine);
            return hash.ToHashCode();
        }
    }

    private readonly record struct InlineSuppression(
        IReadOnlyDictionary<int, Dictionary<string, SuppressionAnchor>> NextLineRuleSuppressions,
        IReadOnlyDictionary<string, SuppressionAnchor> FileRuleSuppressions,
        IReadOnlyDictionary<string, Dictionary<string, SuppressionAnchor>> JobRuleSuppressions,
        IReadOnlyList<JobScope> JobScopes,
        byte[] Source,
        IReadOnlyList<Diagnostic> ConfigurationDiagnostics)
    {
        public static InlineSuppression Empty { get; } = new(
            new Dictionary<int, Dictionary<string, SuppressionAnchor>>(),
            new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal),
            new Dictionary<string, Dictionary<string, SuppressionAnchor>>(StringComparer.Ordinal),
            [],
            [],
            []);
    }

    private readonly record struct JobScope(Utf8Slice JobIdSlice, int StartLine, int EndLine);

    private readonly record struct SuppressionAnchor(int Line, int Column);

    private readonly record struct RulesNormalization(
        IReadOnlyDictionary<string, RuleConfig>? Rules,
        IReadOnlyList<Diagnostic> ConfigurationDiagnostics)
    {
        public static RulesNormalization Empty { get; } = new(null, []);
    }

    private readonly record struct ExclusionsNormalization(
        IReadOnlyList<NormalizedExclusion> Exclusions,
        string NormalizedFilePath,
        Diagnostic[] ConfigurationDiagnostics)
    {
        public static ExclusionsNormalization Empty { get; } = new([], string.Empty, []);
    }

    private readonly record struct NormalizedExclusion(
        string File,
        IReadOnlySet<string>? Rules,
        IReadOnlyList<string>? Jobs);
}
