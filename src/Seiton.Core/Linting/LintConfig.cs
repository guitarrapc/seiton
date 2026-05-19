using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using System.Runtime.CompilerServices;

namespace Seiton.Core.Linting;

/// <summary>
/// The fully normalized configuration model for the lint engine, including rule overrides, exclusions,
/// fix settings, and network options. Produced by <see cref="LintConfigLibrary.Validate"/>.
/// </summary>
public sealed class LintConfig
{
    /// <summary>Gets an empty configuration instance with default values.</summary>
    public static LintConfig Empty { get; } = new();

    /// <summary>Gets the raw UTF-8 YAML bytes being linted (used for expression caching and fix generation).</summary>
    public byte[]? Utf8Yaml { get; set; }

    /// <summary>Gets the AST arena from the parse result (used for per-run shared data).</summary>
    internal AstArena? Arena { get; set; }

    /// <summary>Gets the file path of the document being linted.</summary>
    public string? FilePath { get; set; }

    private Dictionary<long, ExpressionCacheEntry>? _expressionCache;
    private int[]? _lineStarts;
    private long _sourceContentHash;

    /// <summary>
    /// Maximum number of cached expression parse results. When exceeded the cache is
    /// cleared to bound memory in long-lived processes (e.g. WASM playground).
    /// </summary>
    private const int MaxExpressionCacheEntries = 512;

    /// <summary>
    /// Parses an expression with content-based deduplication. Expressions with identical
    /// byte content share the same parse result, even across different source documents.
    /// </summary>
    public ExpressionParseResult ParseExpression(ReadOnlySpan<byte> expression)
    {
        if (expression.IsEmpty)
        {
            return ExpressionParser.Parse(expression);
        }

        var key = ComputeContentHash(expression);

        _expressionCache ??= new();
        if (_expressionCache.TryGetValue(key, out var entry))
        {
            // Collision guard: full byte comparison to guarantee correctness
            if (expression.SequenceEqual(entry.ExpressionBytes))
            {
                return entry.Result;
            }

            // Hash collision with different content — parse without caching (extremely rare)
            return ExpressionParser.Parse(expression);
        }

        var result = ExpressionParser.Parse(expression);

        // Evict all entries when the cache exceeds the cap to bound memory
        if (_expressionCache.Count >= MaxExpressionCacheEntries)
        {
            _expressionCache.Clear();
        }

        _expressionCache[key] = new ExpressionCacheEntry(expression.ToArray(), result);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeContentHash(ReadOnlySpan<byte> span)
    {
        return (long)XxHash64.Hash(span);
    }

    private readonly record struct ExpressionCacheEntry(byte[] ExpressionBytes, ExpressionParseResult Result);

    /// <summary>
    /// Returns the line-start offset array for Utf8Yaml, lazily built on first access.
    /// Shared across all rules in a single lint run.
    /// </summary>
    public int[] GetLineStarts()
    {
        if (_lineStarts is not null)
        {
            return _lineStarts;
        }

        _lineStarts = Utf8Yaml is null ? [] : ExpressionScanHelpers.BuildLineStarts(Utf8Yaml);
        return _lineStarts;
    }

    /// <summary>Gets the rule configurations keyed by rule ID string.</summary>
    public IReadOnlyDictionary<string, RuleConfig>? Rules { get => _rules; init => _rules = value; }
    private IReadOnlyDictionary<string, RuleConfig>? _rules;

    /// <summary>Gets the list of exclusion entries from the config.</summary>
    public IReadOnlyList<LintExclusion>? Exclusions { get; init; }

    /// <summary>Gets the fix configuration section.</summary>
    public FixConfig Fix { get => _fix; init => _fix = value; }
    private FixConfig _fix = new();

    /// <summary>Gets the network configuration section.</summary>
    public NetworkConfig Network { get => _network; init => _network = value; }
    private NetworkConfig _network = new();

    /// <summary>Gets the output configuration section.</summary>
    public OutputConfig Output { get => _output; init => _output = value; }
    private OutputConfig _output = new();

    /// <summary>
    /// When <c>true</c>, the <see cref="LintResult.SuppressionSummary"/> is set to
    /// <see cref="SuppressionSummary.Empty"/> even when diagnostics are suppressed.
    /// Suppression filtering still occurs (suppressed diagnostics are removed), but
    /// the per-rule breakdown and record array are not materialized.
    /// Use this in memory-constrained environments (e.g. WASM Playground) where the
    /// suppression summary is never consumed.
    /// </summary>
    public bool SkipSuppressionSummary { get; init; }

    /// <summary>
    /// When <c>true</c>, rules may emit additional informational diagnostics (e.g. ignored actions).
    /// Corresponds to the CLI <c>--verbose</c> flag.
    /// </summary>
    public bool Verbose { get; set; }

    private static readonly FixConfig DefaultFix = new();
    private static readonly NetworkConfig DefaultNetwork = new();
    private static readonly OutputConfig DefaultOutput = new();

    /// <summary>
    /// Resets per-call state and updates properties for a new lint run.
    /// Preserves expression cache across source changes (cache keys are content-hash-based,
    /// collision guard uses full byte comparison, and entry count is capped at
    /// <see cref="MaxExpressionCacheEntries"/> to bound memory).
    /// Line starts are recomputed when the source content changes.
    /// Safe even when the same byte[] is reused with different content.
    /// </summary>
    internal void PrepareForRun(
        byte[] utf8Yaml,
        AstArena? arena,
        string filePath,
        IReadOnlyDictionary<string, RuleConfig>? rules,
        FixConfig? fix,
        NetworkConfig? network,
        OutputConfig? output,
        bool verbose = false)
    {
        var contentHash = ComputeContentHash(utf8Yaml);
        var sameContent = contentHash == _sourceContentHash
            && Utf8Yaml is not null
            && Utf8Yaml.Length == utf8Yaml.Length
            && Utf8Yaml.AsSpan().SequenceEqual(utf8Yaml);
        _sourceContentHash = contentHash;
        Utf8Yaml = utf8Yaml;
        Arena = arena;
        FilePath = filePath;
        _rules = rules;
        _fix = fix ?? DefaultFix;
        _network = network ?? DefaultNetwork;
        _output = output ?? DefaultOutput;
        Verbose = verbose;
        if (!sameContent)
        {
            _lineStarts = null;
        }
    }

    /// <summary>Looks up the rule configuration for the specified <paramref name="ruleId"/>.</summary>
    public RuleConfig? GetRuleConfig(string ruleId)
    {
        if (Rules is null || !Rules.TryGetValue(ruleId, out var config))
            return null;
        return config;
    }

    /// <summary>Looks up the rule configuration for the specified <paramref name="ruleId"/> enum value.</summary>
    public RuleConfig? GetRuleConfig(RuleId ruleId) => GetRuleConfig(ruleId.ToId());
}

/// <summary>Per-rule configuration: enabled state, severity override, and rule-specific options.</summary>
public sealed record RuleConfig
{
    /// <summary>Gets whether the rule is enabled. Defaults to <c>true</c>.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Gets the user-specified severity override, if any.</summary>
    public DiagnosticSeverity? Severity { get; init; }

    /// <summary>Gets the extendable event list for <c>dangerous-triggers</c>.</summary>
    public ExtendableList? Events { get; init; }
    /// <summary>Gets the extendable label list for <c>runner-label</c>.</summary>
    public ExtendableList? KnownHostedLabels { get; init; }
    /// <summary>Gets the extendable registry list for <c>credentials</c>.</summary>
    public ExtendableList? PublicRegistries { get; init; }
    /// <summary>Gets the extendable trigger list for <c>cache-poisoning</c>.</summary>
    public ExtendableList? UntrustedTriggers { get; init; }
    /// <summary>Gets the extendable output command list for <c>unredacted-secrets</c>.</summary>
    public ExtendableList? OutputCommands { get; init; }

    /// <summary>Gets the assume-events list for <c>expr-undefined-var</c>.</summary>
    public IReadOnlyList<string>? AssumeEvents { get; init; }
    /// <summary>Gets the allow patterns for <c>forbidden-uses</c>.</summary>
    public IReadOnlyList<string>? Allow { get; init; }
    /// <summary>Gets the deny patterns for <c>forbidden-uses</c>.</summary>
    public IReadOnlyList<string>? Deny { get; init; }

    /// <summary>Gets the max step env secrets threshold for <c>overprovisioned-secrets</c>.</summary>
    public int? MaxStepEnvSecrets { get; init; }
    /// <summary>Gets the max job secrets threshold for <c>overprovisioned-secrets</c>.</summary>
    public int? MaxJobSecrets { get; init; }

    /// <summary>Gets the ignore-actions patterns for <c>unpinned-uses</c>.</summary>
    public IReadOnlyList<IgnoreActionRule>? IgnoreActions { get; init; }
}

/// <summary>
/// An ignore-actions entry for the <c>unpinned-uses</c> rule.
/// When <see cref="Refs"/> is null, all refs are ignored (string-form backward compat).
/// When non-null, only the listed refs trigger the ignore (ref-conditional).
/// </summary>
/// <param name="Pattern">Glob pattern matched against <c>owner/repo</c> (case-insensitive).</param>
/// <param name="Refs">When null, all refs are ignored. When non-null, only these exact refs are ignored (case-sensitive).</param>
public sealed record IgnoreActionRule(string Pattern, IReadOnlyList<string>? Refs = null);

/// <summary>A list that extends (appends to) a rule's built-in defaults, matching the YAML <c>extend:</c> key.</summary>
public sealed record ExtendableList(IReadOnlyList<string> Extend);

/// <summary>An exclusion entry that suppresses rules for matching files/jobs.</summary>
/// <remarks>
/// <para><c>Rules</c> = <c>null</c>: all rules are suppressed (file/job-level exclusion).</para>
/// <para><c>Rules</c> = non-null list: only those rules are suppressed.</para>
/// </remarks>
public sealed record LintExclusion(
    string File,
    IReadOnlyList<string>? Rules,
    IReadOnlyList<string>? Jobs = null);

/// <summary>Configuration for the <c>fix:</c> section controlling auto-fix behavior.</summary>
public sealed record FixConfig
{
    /// <summary>
    /// When true, rules will build DiagnosticFix objects during Check().
    /// Defaults to false so lint-only runs skip fix construction overhead.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the defaults sub-section of the fix configuration.</summary>
    public FixDefaultsConfig Defaults { get; init; } = new();
    /// <summary>Gets the pinning sub-section of the fix configuration.</summary>
    public FixPinningConfig Pinning { get; init; } = new();
    /// <summary>Gets the images sub-section of the fix configuration.</summary>
    public FixImagesConfig Images { get; init; } = new();
}

/// <summary>Default values applied by the fix engine (e.g. <c>job-timeout-minutes</c>).</summary>
public sealed record FixDefaultsConfig
{
    /// <summary>Gets the default job timeout in minutes to apply during fix, if any.</summary>
    public int? JobTimeoutMinutes { get; init; }
}

/// <summary>Configuration for action/workflow pinning remediation.</summary>
public sealed record FixPinningConfig
{
    /// <summary>Gets whether network access is enabled for SHA resolution.</summary>
    public bool EnableNetwork { get; init; }
    /// <summary>Gets whether <c>enable-network</c> was explicitly present in config.</summary>
    public bool HasEnableNetwork { get; init; }
    /// <summary>Gets the minimum age in days for an action reference to be eligible for pinning.</summary>
    public int MinAgeDays { get; init; } = 14;
    /// <summary>Gets the branches excluded from pinning fix application.</summary>
    public IReadOnlyList<string> ExcludeBranches { get; init; } = ["main", "master"];
    /// <summary>Gets the action patterns to ignore during pinning.</summary>
    public IReadOnlyList<IgnoreActionEntry> IgnoreActions { get; init; } = [];
}

/// <summary>Configuration for container image pinning remediation.</summary>
public sealed record FixImagesConfig
{
    private static readonly IReadOnlyList<string> DefaultExcludeImages = ["scratch"];
    private static readonly IReadOnlyList<string> DefaultExcludeTags = ["latest"];

    /// <summary>Gets whether network access is enabled for OCI image digest resolution.</summary>
    public bool EnableNetwork { get; init; }
    /// <summary>Gets whether <c>enable-network</c> was explicitly present in config.</summary>
    public bool HasEnableNetwork { get; init; }

    private IReadOnlyList<string> _excludeImages = DefaultExcludeImages;

    /// <summary>Gets the image names to exclude from digest pinning. Always includes <c>scratch</c>.</summary>
    public IReadOnlyList<string> ExcludeImages
    {
        get => _excludeImages;
        init => _excludeImages = EnforceScratch(value);
    }

    /// <summary>Gets the tags to exclude from digest pinning.</summary>
    public IReadOnlyList<string> ExcludeTags { get; init; } = DefaultExcludeTags;
    /// <summary>Gets the image glob patterns to ignore entirely.</summary>
    public IReadOnlyList<string> IgnoreImages { get; init; } = [];

    private static IReadOnlyList<string> EnforceScratch(IReadOnlyList<string> values)
    {
        if (values.Contains("scratch"))
            return values;
        var list = new List<string>(values) { "scratch" };
        return list.AsReadOnly();
    }
}

/// <summary>Network behavior configuration (timeouts, concurrency, error handling).</summary>
public sealed record NetworkConfig
{
    /// <summary>Gets the error handling mode for network failures.</summary>
    public NetworkErrorMode OnError { get; init; } = NetworkErrorMode.Skip;
    /// <summary>Gets the timeout in seconds for network requests.</summary>
    public int TimeoutSeconds { get; init; } = 30;
    /// <summary>Gets the maximum number of concurrent network requests.</summary>
    public int MaxConcurrency { get; init; } = LintConfigResourceLimits.DefaultNetworkMaxConcurrency;
    /// <summary>Gets the GitHub-specific network configuration.</summary>
    public GitHubNetworkConfig GitHub { get; init; } = new();
}

public enum NetworkErrorMode { Skip, Fail }

/// <summary>GitHub-specific network settings (GHES API URL, fallback behavior).</summary>
public sealed record GitHubNetworkConfig
{
    /// <summary>Gets the GitHub Enterprise Server API URL, if using GHES.</summary>
    public string? GhesApiUrl { get; init; }
    /// <summary>Gets whether to fall back to the public GitHub API when GHES fails.</summary>
    public bool GhesFallback { get; init; }
}

/// <summary>Configuration for the <c>output:</c> section controlling diagnostic output behavior.</summary>
public sealed record OutputConfig
{
    /// <summary>Gets the diagnostic sort order. Defaults to <see cref="DiagnosticSortOrder.Location"/>.</summary>
    public DiagnosticSortOrder SortOrder { get; init; } = DiagnosticSortOrder.Location;
}

/// <summary>Specifies how diagnostics are sorted in the final output.</summary>
public enum DiagnosticSortOrder
{
    /// <summary>Sort by source location (line, column) with rule ID as tiebreaker. This is the default.</summary>
    Location,
    /// <summary>Sort by rule priority first, then severity, then location.</summary>
    Rule,
}
