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
    private readonly List<string> _disabledRuleIds = new(8);

    // NormalizeRules reusable collections
    private readonly Dictionary<string, RuleConfig> _normalizedRulesDict = new(StringComparer.Ordinal);

    // Shared config diagnostics buffer: used sequentially by NormalizeRules, NormalizeExclusions,
    // and ParseInlineSuppression. Each caller Clear()s before use and the result is consumed via
    // AddRange before the next caller runs.
    private readonly List<Diagnostic> _configDiagnostics = new();

    // ParseInlineSuppression reusable collections
    private readonly Dictionary<int, Dictionary<string, SuppressionAnchor>> _nextLineRuleSuppressions = new();
    private readonly Dictionary<int, Dictionary<string, SuppressionAnchor>> _stepRuleSuppressions = new();
    private readonly Dictionary<string, SuppressionAnchor> _fileRuleSuppressions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, SuppressionAnchor>> _jobRuleSuppressions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StepScope> _stepScopes = new();
    private readonly List<JobScope> _jobScopes = new();

    // S-6: Reusable buffer for BuildKnownJobIdSlices (avoids per-call allocation)
    private Utf8Slice[] _knownJobIdSlices = new Utf8Slice[8];

    // A-8: NormalizeExclusions reusable collections
    private readonly List<NormalizedExclusion> _normalizedExclusions = new(4);

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

    /// <summary>
    /// Lints a pre-parsed workflow using an existing <see cref="ParseResult"/>.
    /// Use this to avoid re-parsing when you need both parse-only analysis and linting,
    /// or when implementing parser-only / linter-only / combined pipelines.
    /// </summary>
    /// <param name="parseResult">
    /// A parse result obtained from <see cref="WorkflowParser.Parse(byte[], string)"/>.
    /// Ownership remains with the caller. Keep <paramref name="parseResult"/> alive and undisposed
    /// until you are finished reading from and disposing the returned <see cref="LintResult"/>,
    /// because the lint result borrows the parse result's arena for string/AST resolution.
    /// </param>
    /// <param name="utf8Yaml">The original UTF-8 YAML bytes (must be the same bytes used for parsing).</param>
    /// <param name="filePath">
    /// File path for diagnostic messages and document kind hinting.
    /// When <paramref name="parseResult"/> has no workflow/action AST (for example after a fatal parse error),
    /// the path hint is used to preserve <see cref="LintResult.DocumentKind"/> metadata.
    /// </param>
    /// <param name="config">Optional lint configuration.</param>
    /// <returns>A lint result. Dispose when done reading diagnostics.</returns>
    public LintResult Check(ParseResult parseResult, byte[] utf8Yaml, string filePath, LintConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        // Fail fast when caller passes different bytes than what was parsed.
        // The arena stores a reference to the original source — reference equality is O(1).
        var arena = parseResult.Arena;
        if (!ReferenceEquals(utf8Yaml, arena.Source))
        {
            throw new ArgumentException(
                "utf8Yaml must be the same array instance that was passed to WorkflowParser.Parse. " +
                "Passing different bytes causes inconsistent expression artifacts, fix offsets, and line starts.",
                nameof(utf8Yaml));
        }

        var data = CheckWithParseResult(utf8Yaml, filePath, config, parseResult.Data, arena);
        return new LintResult(data, arena, ownsArena: false); // caller owns ParseResult's arena
    }

    /// <summary>Parses and lints the given YAML, applying the optional <paramref name="config"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Document kind metadata:</b> <see cref="LintResult.DocumentKind"/> prefers the parser's finalized
    /// classification. When final classification is unknown, the engine falls back to the parser path hint kind
    /// so files such as <c>action.yml</c> still return stable document-kind and rule-activation metadata even when
    /// parsing fails before an AST is produced.
    /// </para>
    /// <para>
    /// This fallback affects result metadata only; fatal parse errors still short-circuit before any rule traversal.
    /// </para>
    /// <para>
    /// <b>Thread safety:</b> A single <see cref="LintEngine"/> instance is <b>not</b> safe for concurrent
    /// <see cref="Check"/> calls. Internal mutable state (diagnostics lists, visitor, rule instances, caches)
    /// is cleared and reused on every call. For parallel multi-file linting, use
    /// <c>ThreadLocal&lt;LintEngine&gt;</c> so each thread owns an independent instance.
    /// The caller-provided <paramref name="config"/> is read-only from the engine's perspective — each engine
    /// copies relevant settings into its own internal <c>_effectiveConfig</c> via <c>PrepareForRun</c>.
    /// </para>
    /// <para>
    /// <b>Result lifetime:</b> The returned <see cref="LintResult"/> owns the underlying
    /// <see cref="AstArena"/>. Use <c>using var result = engine.Check(...);</c> to ensure proper disposal.
    /// Call <see cref="LintResult.CopyDiagnostics"/> to obtain a caller-owned snapshot that outlives the result.
    /// </para>
    /// </remarks>
    public LintResult Check(byte[] utf8Yaml, string filePath, LintConfig? config)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var classifiedParseResult = WorkflowParser.ParseClassified(utf8Yaml, filePath, out var arena);
        var parseResult = classifiedParseResult.ParseResult;
        var classification = classifiedParseResult.Classification;
        var documentKind = classification.FinalKind != DocumentKind.Unknown
            ? classification.FinalKind
            : classification.PathHintKind;
        var lintResult = CheckCore(utf8Yaml, filePath, config, parseResult, arena, documentKind);
        return new LintResult(lintResult, arena);
    }

    /// <summary>
    /// Parses and lints the given YAML, returning the result with the arena as an out parameter.
    /// Used by internal callers that need explicit arena ownership without the <see cref="LintResult"/> wrapper.
    /// The caller is responsible for disposing the returned arena.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="Check(byte[], string, LintConfig?)"/>, including path-hint fallback for
    /// <see cref="LintResultData.DocumentKind"/> when parser final classification is unknown.
    /// </remarks>
    internal LintResultData CheckDirect(byte[] utf8Yaml, string filePath, LintConfig? config, out AstArena? arena)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var classifiedParseResult = WorkflowParser.ParseClassified(utf8Yaml, filePath, out arena);
        var parseResult = classifiedParseResult.ParseResult;
        var classification = classifiedParseResult.Classification;
        var documentKind = classification.FinalKind != DocumentKind.Unknown
            ? classification.FinalKind
            : classification.PathHintKind;
        return CheckCore(utf8Yaml, filePath, config, parseResult, arena, documentKind);
    }

    /// <summary>Parses and lints the given YAML with no explicit configuration. Returns result with arena as out parameter.</summary>
    internal LintResultData CheckDirect(byte[] utf8Yaml, string filePath, out AstArena? arena)
    {
        return CheckDirect(utf8Yaml, filePath, config: null, out arena);
    }

    /// <summary>
    /// Lints a pre-parsed <see cref="ParseResultData"/> without re-parsing.
    /// Used by Playground incremental parsing (D-5b) where parsing is done externally.
    /// Infers <see cref="DocumentKind"/> from the parse result content.
    /// </summary>
    /// <param name="skipJobs">
    /// Optional job-skipping mask (D-5d). When <paramref name="skipJobs"/>[i] is true, lint rules
    /// are not run on that job (its diagnostics are expected to be supplied from a cache by the caller).
    /// </param>
    internal LintResultData CheckWithParseResult(byte[] utf8Yaml, string filePath, LintConfig? config, ParseResultData parseResult, AstArena? arena, bool[]? skipJobs = null)
    {
        ArgumentNullException.ThrowIfNull(utf8Yaml);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var kind = InferDocumentKindForPreParsedResult(parseResult, filePath);
        return CheckCore(utf8Yaml, filePath, config, parseResult, arena, kind, skipJobs);
    }

    private static DocumentKind InferDocumentKindForPreParsedResult(ParseResultData parseResult, string filePath)
    {
        if (parseResult.ActionMetadata is not null)
        {
            return DocumentKind.ActionMetadata;
        }

        if (parseResult.Workflow is not null)
        {
            return DocumentKind.Workflow;
        }

        return DocumentKindClassifier.GetPathHintKind(filePath);
    }

    private LintResultData CheckCore(byte[] utf8Yaml, string filePath, LintConfig? config, ParseResultData parseResult, AstArena? arena, DocumentKind documentKind, bool[]? skipJobs = null)
    {
        var normalizedRules = NormalizeRules(config?.Rules, filePath);
        _disabledRuleIds.Clear();

        // File-level exclusion (Rules: null, Jobs: null) short-circuits workflow diagnostics.
        // Parse errors remain suppressed for fully-excluded files, but configuration
        // diagnostics produced while normalizing rules and exclusions must still be reported.
        if (ExclusionMatcher.IsFileFullyExcluded(config?.Exclusions, filePath))
        {
            // Snapshot rule config diagnostics before NormalizeExclusions clears the shared buffer.
            var ruleConfigDiagCount = normalizedRules.ConfigurationDiagnostics.Count;
            Diagnostic[]? ruleConfigDiags = null;
            if (ruleConfigDiagCount > 0)
            {
                ruleConfigDiags = new Diagnostic[ruleConfigDiagCount];
                for (var i = 0; i < ruleConfigDiagCount; i++)
                {
                    ruleConfigDiags[i] = normalizedRules.ConfigurationDiagnostics[i];
                }
            }

            // Always normalize exclusions when arena is available — this validates rule IDs
            // and file patterns even when the workflow failed to parse.
            // Job-ID validation is skipped internally when no jobs are known.
            var exclusionNormResult = arena is not null
                ? NormalizeExclusions(config?.Exclusions, filePath, parseResult.Workflow ?? EmptyWorkflowForSuppression, utf8Yaml, arena, config?.ConfigFilePath)
                : ExclusionsNormalization.Empty;

            var configDiagnosticCount = ruleConfigDiagCount + exclusionNormResult.ConfigurationDiagnostics.Length;

            DiagnosticList diagnostics = default;
            if (configDiagnosticCount > 0)
            {
                var configurationDiagnostics = new Diagnostic[configDiagnosticCount];
                var diagnosticIndex = 0;
                if (ruleConfigDiags is not null)
                {
                    for (var i = 0; i < ruleConfigDiags.Length; i++)
                    {
                        configurationDiagnostics[diagnosticIndex++] = ruleConfigDiags[i];
                    }
                }
                for (var i = 0; i < exclusionNormResult.ConfigurationDiagnostics.Length; i++)
                {
                    configurationDiagnostics[diagnosticIndex++] = exclusionNormResult.ConfigurationDiagnostics[i];
                }

                diagnostics = new DiagnosticList(configurationDiagnostics);
            }

            var (ruleCount, disabledIds) = GetRuleActivationMetadataForDocumentKind(normalizedRules.Rules, documentKind);
            // Suppress parse diagnostics and fatal flag for fully-excluded files.
            // ParseDiagnostics/HasFatalError must not leak suppressed parse state.
            var suppressedParseResult = parseResult with { Diagnostics = default, HasFatalError = false };
            return new LintResultData(suppressedParseResult, diagnostics)
            {
                SuppressionSummary = SuppressionSummary.Empty,
                DocumentKind = documentKind,
                ActiveRuleCount = ruleCount,
                DisabledRuleCount = disabledIds.Length,
                DisabledRuleIds = disabledIds,
            };
        }

        if (parseResult.HasFatalError || (parseResult.Workflow is null && parseResult.ActionMetadata is null))
        {
            DiagnosticList diagnostics = parseResult.Diagnostics;
            if (normalizedRules.ConfigurationDiagnostics.Count > 0)
            {
                var mergedDiagnostics = new Diagnostic[parseResult.Diagnostics.Length + normalizedRules.ConfigurationDiagnostics.Count];
                parseResult.Diagnostics.AsSpan().CopyTo(mergedDiagnostics);
                for (var i = 0; i < normalizedRules.ConfigurationDiagnostics.Count; i++)
                {
                    mergedDiagnostics[parseResult.Diagnostics.Length + i] = normalizedRules.ConfigurationDiagnostics[i];
                }

                diagnostics = new DiagnosticList(mergedDiagnostics);
            }

            var (parseErrorActiveRuleCount, parseErrorDisabledRuleIds) = GetRuleActivationMetadataForDocumentKind(normalizedRules.Rules, documentKind);
            return new LintResultData(parseResult, diagnostics)
            {
                SuppressionSummary = SuppressionSummary.Empty,
                DocumentKind = documentKind,
                ActiveRuleCount = parseErrorActiveRuleCount,
                DisabledRuleCount = parseErrorDisabledRuleIds.Length,
                DisabledRuleIds = parseErrorDisabledRuleIds,
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

        _diagnostics.AddRange(normalizedRules.ConfigurationDiagnostics);

        _effectiveConfig.PrepareForRun(
            utf8Yaml,
            arena,
            filePath,
            normalizedRules.Rules,
            config?.Fix,
            config?.Network,
            config?.Output,
            config?.Verbose ?? false,
            parseResult.ExpressionArtifacts);
        var effectiveConfig = _effectiveConfig;

        var workflowForSuppression = parseResult.Workflow ?? EmptyWorkflowForSuppression;
        var inlineSuppression = ParseInlineSuppression(utf8Yaml, filePath, workflowForSuppression, parseResult.ActionMetadata, arena!);
        _diagnostics.AddRange(inlineSuppression.ConfigurationDiagnostics);

        var normalizedExclusions = NormalizeExclusions(config?.Exclusions, filePath, workflowForSuppression, utf8Yaml, arena!, config?.ConfigFilePath);
        _diagnostics.AddRange(normalizedExclusions.ConfigurationDiagnostics);

        if (rules.Count == 0 && _onlineRules.Count == 0)
        {
            // Rule activation metadata is relative to the engine's installed rule set.
            // A custom engine constructed with no rules therefore reports zero active/
            // disabled rules even if config mentions rule ids that are unknown here.
            return BuildLintResult(parseResult, arena, documentKind, 0, 0, []);
        }

        _visitor.Reset();
        var sharedDisabledRuleIds = effectiveConfig.Rules is null || effectiveConfig.Rules.Count == 0
            ? GetSharedDefaultDisabledRuleIds(documentKind)
            : null;
        var (activeRuleCount, disabledRuleCount, disabledRuleIdsSnapshot) = ConfigureRuleActivation(documentKind, effectiveConfig, effectiveConfig.Rules, sharedDisabledRuleIds);

        if (_activeRules.Count == 0 && _activeOnlineRules.Count == 0)
        {
            return BuildLintResult(parseResult, arena, documentKind, activeRuleCount, disabledRuleCount, disabledRuleIdsSnapshot);
        }

        if (parseResult.Workflow is not null)
        {
            _visitor.Visit(parseResult.Workflow, skipJobs);
        }
        else if (parseResult.ActionMetadata is not null)
        {
            _visitor.VisitActionMetadata(parseResult.ActionMetadata);
        }

        return FinalizeRuleDiagnostics(
            parseResult,
            arena,
            documentKind,
            effectiveConfig.Rules,
            inlineSuppression,
            normalizedExclusions,
            effectiveConfig.Output.SortOrder,
            activeRuleCount,
            disabledRuleCount,
            disabledRuleIdsSnapshot,
            skipSuppressionSummary: config?.SkipSuppressionSummary == true);
    }

    private LintResultData FinalizeRuleDiagnostics(
        ParseResultData parseResult,
        AstArena? arena,
        DocumentKind documentKind,
        IReadOnlyDictionary<string, RuleConfig>? effectiveRules,
        InlineSuppression inlineSuppression,
        ExclusionsNormalization normalizedExclusions,
        DiagnosticSortOrder sortOrder,
        int activeRuleCount,
        int disabledRuleCount,
        string[] disabledRuleIdsSnapshot,
        bool skipSuppressionSummary)
    {
        _ruleDiagnostics.Clear();
        for (var i = 0; i < _activeRules.Count; i++)
        {
            var currentRuleDiagnostics = _activeRules[i].GetDiagnostics();
            for (var j = 0; j < currentRuleDiagnostics.Count; j++)
            {
                var current = currentRuleDiagnostics[j];
                if (TryGetSeverityOverride(current.RuleId, effectiveRules, out var severityOverride))
                {
                    current = current with { Severity = severityOverride };
                }

                _ruleDiagnostics.Add(current);
            }
        }

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

        if (skipSuppressionSummary)
        {
            return BuildLintResult(parseResult, arena, documentKind, activeRuleCount, disabledRuleCount, disabledRuleIdsSnapshot);
        }

        return BuildLintResultWithSuppression(parseResult, arena, documentKind, activeRuleCount, disabledRuleCount, disabledRuleIdsSnapshot);
    }

    private (int ActiveRuleCount, string[] DisabledRuleIds) GetRuleActivationMetadataForDocumentKind(
        IReadOnlyDictionary<string, RuleConfig>? normalizedRuleConfig,
        DocumentKind documentKind)
    {
        if ((normalizedRuleConfig is null || normalizedRuleConfig.Count == 0)
            && RuleCatalog.MatchesDefaultRuleSet(rules, _onlineRules))
        {
            return (RuleCatalog.GetDefaultActiveRuleCount(documentKind), RuleCatalog.GetDefaultDisabledRuleIds(documentKind));
        }

        _disabledRuleIds.Clear();

        var activeRuleCount = 0;
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var ruleId = rule.Id.ToId() ?? throw new InvalidOperationException($"Rule {rule.Id} must provide a non-null id.");
            var supportsDocumentKind = rule.SupportsDocumentKind(documentKind);
            if (!IsRuleEnabled(ruleId, normalizedRuleConfig))
            {
                if (supportsDocumentKind)
                {
                    _disabledRuleIds.Add(ruleId);
                }

                continue;
            }

            if (supportsDocumentKind)
            {
                activeRuleCount++;
            }
        }

        for (var i = 0; i < _onlineRules.Count; i++)
        {
            var onlineRule = _onlineRules[i];
            var ruleId = onlineRule.Id.ToId() ?? throw new InvalidOperationException($"Rule {onlineRule.Id} must provide a non-null id.");
            var supportsDocumentKind = onlineRule.SupportsDocumentKind(documentKind);
            if (!IsRuleEnabled(ruleId, normalizedRuleConfig))
            {
                if (supportsDocumentKind)
                {
                    _disabledRuleIds.Add(ruleId);
                }

                continue;
            }

            if (supportsDocumentKind)
            {
                activeRuleCount++;
            }
        }

        return (activeRuleCount, _disabledRuleIds.Count > 0 ? _disabledRuleIds.ToArray() : []);
    }

    private (int ActiveRuleCount, int DisabledRuleCount, string[] DisabledRuleIds) ConfigureRuleActivation(
        DocumentKind documentKind,
        LintConfig effectiveConfig,
        IReadOnlyDictionary<string, RuleConfig>? effectiveRules,
        string[]? sharedDisabledRuleIds)
    {
        _activeRules.Clear();
        _activeOnlineRules.Clear();
        _disabledRuleIds.Clear();

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var ruleId = rule.Id.ToId() ?? throw new InvalidOperationException($"Rule {rule.Id} must provide a non-null id.");
            var supportsDocumentKind = rule.SupportsDocumentKind(documentKind);
            if (!IsRuleEnabled(ruleId, effectiveRules))
            {
                if (sharedDisabledRuleIds is null && supportsDocumentKind)
                {
                    _disabledRuleIds.Add(ruleId);
                }

                continue;
            }

            if (!supportsDocumentKind)
            {
                continue;
            }

            rule.SetConfig(effectiveConfig);
            _visitor.AddPass(rule);
            _activeRules.Add(rule);
        }

        for (var i = 0; i < _onlineRules.Count; i++)
        {
            var onlineRule = _onlineRules[i];
            var ruleId = onlineRule.Id.ToId() ?? throw new InvalidOperationException($"Rule {onlineRule.Id} must provide a non-null id.");
            var supportsDocumentKind = onlineRule.SupportsDocumentKind(documentKind);
            if (!IsRuleEnabled(ruleId, effectiveRules))
            {
                if (sharedDisabledRuleIds is null && supportsDocumentKind)
                {
                    _disabledRuleIds.Add(ruleId);
                }

                continue;
            }

            if (!supportsDocumentKind)
            {
                continue;
            }

            onlineRule.SetConfig(effectiveConfig);
            _visitor.AddPass(onlineRule);
            _activeOnlineRules.Add(onlineRule);
        }

        var activeRuleCount = _activeRules.Count + _activeOnlineRules.Count;
        if (sharedDisabledRuleIds is not null)
        {
            return (activeRuleCount, sharedDisabledRuleIds.Length, sharedDisabledRuleIds);
        }

        var disabledRuleCount = _disabledRuleIds.Count;
        return (activeRuleCount, disabledRuleCount, disabledRuleCount > 0 ? _disabledRuleIds.ToArray() : []);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string[]? GetSharedDefaultDisabledRuleIds(DocumentKind documentKind)
    {
        return RuleCatalog.MatchesDefaultRuleSet(rules, _onlineRules)
            ? RuleCatalog.GetDefaultDisabledRuleIds(documentKind)
            : null;
    }

    /// <summary>
    /// Copies <c>_diagnostics</c> into an exact-sized array using a two-buffer swap pattern.
    /// When the previous result's array (now in <c>_resultDiagnosticsSwap</c>) has the right length,
    /// it is reused with zero allocation. Otherwise a new array is allocated.
    /// </summary>
    private LintResultData BuildLintResult(ParseResultData parseResult, AstArena? arena, DocumentKind documentKind, int activeRuleCount, int disabledRuleCount, string[] disabledRuleIds)
    {
        var count = _diagnostics.Count;
        var buffer = new PooledBuffer<Diagnostic>(count > 0 ? count : 4);
        for (var i = 0; i < count; i++)
        {
            buffer.Add(_diagnostics[i]);
        }

        var (diagArray, diagCount) = buffer.DetachArray();
        buffer.Dispose();
        arena?.RegisterLintDiagnosticsBuffer(diagArray);

        return new LintResultData(parseResult, new DiagnosticList(diagArray, diagCount))
        {
            SuppressionSummary = SuppressionSummary.Empty,
            DocumentKind = documentKind,
            ActiveRuleCount = activeRuleCount,
            DisabledRuleCount = disabledRuleCount,
            DisabledRuleIds = disabledRuleIds,
        };
    }

    /// <summary>
    /// Builds a <see cref="LintResultData"/> with suppression summary using PooledBuffer + DetachArray.
    /// </summary>
    private LintResultData BuildLintResultWithSuppression(ParseResultData parseResult, AstArena? arena, DocumentKind documentKind, int activeRuleCount, int disabledRuleCount, string[] disabledRuleIds)
    {
        var count = _diagnostics.Count;
        var buffer = new PooledBuffer<Diagnostic>(count > 0 ? count : 4);
        for (var i = 0; i < count; i++)
        {
            buffer.Add(_diagnostics[i]);
        }

        var (diagArray, diagCount) = buffer.DetachArray();
        buffer.Dispose();
        arena?.RegisterLintDiagnosticsBuffer(diagArray);

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

        return new LintResultData(parseResult, new DiagnosticList(diagArray, diagCount))
        {
            SuppressionSummary = new SuppressionSummary(suppressionCount, suppressedByRuleSnapshot, suppressionRecordsSnapshot),
            DocumentKind = documentKind,
            ActiveRuleCount = activeRuleCount,
            DisabledRuleCount = disabledRuleCount,
            DisabledRuleIds = disabledRuleIds,
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

        if (inlineSuppression.StepRuleSuppressions.Count != 0
            && TryFindStepStartLineForLine(diagnostic.Location.StartLine, inlineSuppression.StepScopes, out var stepStartLine)
            && inlineSuppression.StepRuleSuppressions.TryGetValue(stepStartLine, out var stepSuppressedRuleIds)
            && stepSuppressedRuleIds.TryGetValue(diagnostic.RuleId, out var stepAnchor))
        {
            suppressionRecord = new SuppressionRecord(
                diagnostic.RuleId,
                SuppressionSource.InlineStep,
                stepAnchor.Line,
                stepAnchor.Column,
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
        // Iterate in reverse: scopes are in YAML order (ascending StartLine).
        // When ranges overlap at boundaries (MappingEnd points to next sibling's line),
        // reverse iteration picks the scope with the highest StartLine <= line, which is correct.
        for (var i = jobScopes.Count - 1; i >= 0; i--)
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

    private static bool TryFindStepStartLineForLine(int line, IReadOnlyList<StepScope> stepScopes, out int stepStartLine)
    {
        for (var i = stepScopes.Count - 1; i >= 0; i--)
        {
            var scope = stepScopes[i];
            if (line >= scope.StartLine && line <= scope.EndLine)
            {
                stepStartLine = scope.StartLine;
                return true;
            }
        }

        stepStartLine = 0;
        return false;
    }

    private InlineSuppression ParseInlineSuppression(byte[] utf8Yaml, string filePath, Parsing.Ast.Workflow workflow, ActionMetadata? actionMetadata, AstArena arena)
    {
        if (utf8Yaml.Length == 0)
        {
            return InlineSuppression.Empty;
        }

        // UTF-8 byte constants for directive parsing
        ReadOnlySpan<byte> seitonPrefixUtf8 = "seiton:"u8;
        ReadOnlySpan<byte> disableNextLineUtf8 = "disable-next-line"u8;
        ReadOnlySpan<byte> disableStepUtf8 = "disable-step"u8;
        ReadOnlySpan<byte> disableFileUtf8 = "disable-file"u8;
        ReadOnlySpan<byte> disableJobUtf8 = "disable-job"u8;

        var knownJobIdSlices = BuildKnownJobIdSlices(workflow, arena);
        BuildJobScopes(workflow, arena);
        _stepScopes.Clear();
        var stepScopesBuilt = false;

        // Clear reusable collections; inner dicts of nextLine/job are discarded on Clear
        _nextLineRuleSuppressions.Clear();
        _stepRuleSuppressions.Clear();
        _fileRuleSuppressions.Clear();
        _jobRuleSuppressions.Clear();
        _configDiagnostics.Clear();

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

                    AddRuleIds(argsBytes, argsOffset, suppressedRuleIds, _configDiagnostics, filePath, lineStartOffset, lineNumber);
                }

                lineStartOffset += lineAdvance;
                remaining = remaining[lineAdvance..];
                continue;
            }

            if (commandBytes.SequenceEqual(disableStepUtf8))
            {
                if (!stepScopesBuilt)
                {
                    BuildStepScopes(utf8Yaml, _effectiveConfig.GetLineStarts(), workflow, actionMetadata);
                    stepScopesBuilt = true;
                }

                if (argsBytes.IsEmpty)
                {
                    _configDiagnostics.Add(BuildInlineDirectiveError(
                        "disable-step requires at least one rule-id",
                        filePath,
                        lineStartOffset,
                        lineNumber,
                        commandColumn,
                        commandLen));
                    lineStartOffset += lineAdvance;
                    remaining = remaining[lineAdvance..];
                    continue;
                }

                if (!TryFindNextYamlContentLine(remaining[lineAdvance..], lineNumber, out var targetLine, out var targetIndent, out var targetContent)
                    || targetContent.IsEmpty
                    || targetContent[0] != (byte)'-'
                    || !TryFindStepScopeForItemLine(targetLine, targetIndent, _stepScopes, out var targetStepScope))
                {
                    _configDiagnostics.Add(BuildInlineDirectiveError(
                        "disable-step requires a following step item in the same steps sequence",
                        filePath,
                        lineStartOffset,
                        lineNumber,
                        commandColumn,
                        commandLen));
                    lineStartOffset += lineAdvance;
                    remaining = remaining[lineAdvance..];
                    continue;
                }

                var targetStepStartLine = targetStepScope.StartLine;
                if (!_stepRuleSuppressions.TryGetValue(targetStepStartLine, out var stepSuppressedRuleIds))
                {
                    stepSuppressedRuleIds = new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal);
                    _stepRuleSuppressions[targetStepStartLine] = stepSuppressedRuleIds;
                }

                AddRuleIds(argsBytes, argsOffset, stepSuppressedRuleIds, _configDiagnostics, filePath, lineStartOffset, lineNumber);
                lineStartOffset += lineAdvance;
                remaining = remaining[lineAdvance..];
                continue;
            }

            if (commandBytes.SequenceEqual(disableFileUtf8))
            {
                if (!argsBytes.IsEmpty)
                {
                    AddRuleIds(argsBytes, argsOffset, _fileRuleSuppressions, _configDiagnostics, filePath, lineStartOffset, lineNumber);
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
                    _configDiagnostics.Add(BuildInlineDirectiveError(
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
                    _configDiagnostics.Add(BuildInlineDirectiveError(
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
                    _configDiagnostics.Add(new Diagnostic(
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

                AddRuleIds(ruleIdListBytes, ruleIdListOffset, jobSuppressedRuleIds, _configDiagnostics, filePath, lineStartOffset, lineNumber);
                lineStartOffset += lineAdvance;
                remaining = remaining[lineAdvance..];
                continue;
            }

            // Unknown command
            _configDiagnostics.Add(BuildInlineDirectiveError(
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
            _stepRuleSuppressions,
            _fileRuleSuppressions,
            _jobRuleSuppressions,
            _stepScopes,
            _jobScopes,
            utf8Yaml,
            _configDiagnostics);
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

    private void BuildStepScopes(byte[] source, int[] lineStarts, Parsing.Ast.Workflow workflow, ActionMetadata? actionMetadata)
    {
        _stepScopes.Clear();
        foreach (var pair in workflow.Jobs)
        {
            AddStepScopes(source, lineStarts, pair.Value.Steps);
        }

        AddStepScopes(source, lineStarts, actionMetadata?.Runs?.Steps);
    }

    private void AddStepScopes(byte[] source, int[] lineStarts, IReadOnlyList<Step>? steps)
    {
        if (steps is null)
        {
            return;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var range = steps[i].Range;
            if (range.StartLine <= 0 || range.EndLine <= 0)
            {
                continue;
            }

            if (!TryFindStepItemLineForScope(source, lineStarts, range.StartLine, out var itemLine, out var itemIndent))
            {
                itemLine = range.StartLine;
                itemIndent = 0;
            }

            _stepScopes.Add(new StepScope(itemLine, range.EndLine, itemIndent));
        }
    }

    private static bool TryFindNextYamlContentLine(ReadOnlySpan<byte> remaining, int currentLineNumber, out int lineNumber, out int indent, out ReadOnlySpan<byte> content)
    {
        lineNumber = currentLineNumber;
        indent = 0;
        content = default;
        while (!remaining.IsEmpty)
        {
            lineNumber++;
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

            var lineCore = (!lineBytes.IsEmpty && lineBytes[^1] == (byte)'\r')
                ? lineBytes[..^1]
                : lineBytes;
            var leadingWS = CountLeadingAsciiWhitespace(lineCore);
            var lineContent = lineCore[leadingWS..];
            if (!lineContent.IsEmpty && lineContent[0] != (byte)'#')
            {
                indent = leadingWS;
                content = lineContent;
                return true;
            }

            remaining = remaining[lineAdvance..];
        }

        lineNumber = 0;
        indent = 0;
        content = default;
        return false;
    }

    private static bool TryFindStepScopeForItemLine(int line, int indent, IReadOnlyList<StepScope> stepScopes, out StepScope stepScope)
    {
        for (var i = 0; i < stepScopes.Count; i++)
        {
            var scope = stepScopes[i];
            if (scope.StartLine == line && scope.ItemIndent == indent)
            {
                stepScope = scope;
                return true;
            }
        }

        stepScope = default;
        return false;
    }

    private static bool TryFindStepItemLineForScope(byte[] source, int[] lineStarts, int scopeStartLine, out int itemLineNumber, out int itemIndent)
    {
        itemLineNumber = 0;
        itemIndent = 0;
        for (var currentLine = scopeStartLine; currentLine >= 1 && currentLine <= lineStarts.Length; currentLine--)
        {
            var lineStart = lineStarts[currentLine - 1];
            var lineEnd = currentLine < lineStarts.Length ? lineStarts[currentLine] - 1 : source.Length;
            if (lineEnd > lineStart && source[lineEnd - 1] == (byte)'\r')
            {
                lineEnd--;
            }

            var line = source.AsSpan(lineStart, lineEnd - lineStart);
            var leadingWS = CountLeadingAsciiWhitespace(line);
            var content = line[leadingWS..];
            if (!content.IsEmpty && content[0] == (byte)'-')
            {
                itemLineNumber = currentLine;
                itemIndent = leadingWS;
                return true;
            }
        }

        return false;
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
            var separatorIdx = remaining.IndexOfAny((byte)',', (byte)' ', (byte)'\t');
            ReadOnlySpan<byte> tokenBytes;
            bool hasMore;
            if (separatorIdx >= 0)
            {
                tokenBytes = remaining[..separatorIdx];
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
                    target[internalRuleIdString] = new SuppressionAnchor(lineNumber, tokenColumn);
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

            currentOffset += separatorIdx + 1;
            remaining = remaining[(separatorIdx + 1)..];
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
        _configDiagnostics.Clear();
        RuleNormalizer.NormalizeRuleEntries(rules, filePath, _configDiagnostics, _normalizedRulesDict);
        return new RulesNormalization(_normalizedRulesDict, _configDiagnostics);
    }

    private ExclusionsNormalization NormalizeExclusions(
        IReadOnlyList<LintExclusion>? exclusions,
        string filePath,
        Parsing.Ast.Workflow workflow,
        byte[] utf8Yaml,
        AstArena arena,
        string? configFilePath = null)
    {
        var normalizedFilePath = NormalizePath(filePath);
        if (exclusions is null || exclusions.Count == 0)
        {
            return new ExclusionsNormalization([], normalizedFilePath, []);
        }

        var configurationDiagnosticPath = configFilePath ?? filePath;
        ReadOnlySpan<Utf8Slice> knownJobIdSlices = default;
        var knownJobIdSlicesBuilt = false;
        _normalizedExclusions.Clear();
        _configDiagnostics.Clear();

        for (var i = 0; i < exclusions.Count; i++)
        {
            var exclusion = exclusions[i];
            if (string.IsNullOrWhiteSpace(exclusion.File))
            {
                _configDiagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "exclusion file pattern must not be empty",
                    new TextRange(0, 1, 1, 1, 1, 2),
                    FilePath: configurationDiagnosticPath));
                continue;
            }

            IReadOnlySet<string>? normalizedRuleIds;
            if (exclusion.Rules is null || ExclusionNormalizer.IsAllRulesWildcard(exclusion.Rules))
            {
                // rules omitted or rules: ["*"] → all rules
                normalizedRuleIds = null;
            }
            else
            {
                var ruleIds = new HashSet<string>(StringComparer.Ordinal);
                ExclusionNormalizer.CollectResolvedExclusionRules(exclusion.Rules, configurationDiagnosticPath, _configDiagnostics, ruleIds);

                if (ruleIds.Count == 0)
                {
                    continue;
                }

                normalizedRuleIds = ruleIds;
            }

            var normalizedPattern = NormalizeExclusionPattern(exclusion.File);
            if (exclusion.Jobs is { Count: > 0 }
                && GlobMatch(normalizedPattern, normalizedFilePath))
            {
                if (!knownJobIdSlicesBuilt)
                {
                    knownJobIdSlices = BuildKnownJobIdSlices(workflow, arena);
                    knownJobIdSlicesBuilt = true;
                }

                if (!knownJobIdSlices.IsEmpty)
                {
                    for (var j = 0; j < exclusion.Jobs.Count; j++)
                    {
                        var jobId = exclusion.Jobs[j];
                        if (!string.IsNullOrEmpty(jobId) && !ContainsJobIdOrdinalIgnoreCase(knownJobIdSlices, utf8Yaml, jobId))
                        {
                            _configDiagnostics.Add(new Diagnostic(
                                DiagnosticSeverity.Error,
                                $"unknown job-id '{jobId}' in exclusion configuration",
                                new TextRange(0, jobId.Length, 1, 1, 1, 1 + jobId.Length),
                                FilePath: configurationDiagnosticPath));
                        }
                    }
                }
            }

            _normalizedExclusions.Add(new NormalizedExclusion(normalizedPattern, normalizedRuleIds, exclusion.Jobs));
        }

        return new ExclusionsNormalization(
            _normalizedExclusions.Count > 0 ? _normalizedExclusions.ToArray() : [],
            normalizedFilePath,
            _configDiagnostics.Count > 0 ? _configDiagnostics.ToArray() : []);
    }

    /// <summary>
    /// Normalizes an exclusion file pattern so that repo-root relative paths
    /// (e.g. ".github/workflows/ci.yml") match against absolute file paths.
    /// Patterns that already start with "**/" or are absolute are left as-is.
    /// Relative patterns get "**/" prepended to enable suffix matching.
    /// </summary>
    private static string NormalizeExclusionPattern(string pattern)
    {
        var normalized = NormalizePath(pattern);
        if (normalized.Length == 0)
        {
            return normalized;
        }

        // Already a ** glob — works as-is
        if (normalized == "**" || normalized.StartsWith("**/", StringComparison.Ordinal))
        {
            return normalized;
        }

        // Absolute path (drive letter or root slash) — no prefix needed
        if (normalized[0] == '/' || (normalized.Length >= 2 && normalized[1] == ':'))
        {
            return normalized;
        }

        // Relative pattern: prepend **/ so it matches as a suffix of any absolute path
        return "**/" + normalized;
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
        IReadOnlyDictionary<int, Dictionary<string, SuppressionAnchor>> StepRuleSuppressions,
        IReadOnlyDictionary<string, SuppressionAnchor> FileRuleSuppressions,
        IReadOnlyDictionary<string, Dictionary<string, SuppressionAnchor>> JobRuleSuppressions,
        IReadOnlyList<StepScope> StepScopes,
        IReadOnlyList<JobScope> JobScopes,
        byte[] Source,
        IReadOnlyList<Diagnostic> ConfigurationDiagnostics)
    {
        public static InlineSuppression Empty { get; } = new(
            new Dictionary<int, Dictionary<string, SuppressionAnchor>>(),
            new Dictionary<int, Dictionary<string, SuppressionAnchor>>(),
            new Dictionary<string, SuppressionAnchor>(StringComparer.Ordinal),
            new Dictionary<string, Dictionary<string, SuppressionAnchor>>(StringComparer.Ordinal),
            [],
            [],
            [],
            []);
    }

    private readonly record struct StepScope(int StartLine, int EndLine, int ItemIndent);

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
