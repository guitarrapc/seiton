using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    public byte[]? Utf8Yaml { get; init; }

    /// <summary>Gets the AST arena from the parse result (used for per-run shared data).</summary>
    public AstArena? Arena { get; init; }

    /// <summary>Gets the file path of the document being linted.</summary>
    public string? FilePath { get; init; }

    private Dictionary<long, ExpressionCacheEntry>? _expressionCache;
    private int[]? _lineStarts;

    /// <summary>
    /// Parses an expression with content-based deduplication. Expressions with identical
    /// byte content at different source positions share the same parse result.
    /// The expression span must originate from Utf8Yaml.
    /// </summary>
    public ExpressionParseResult ParseExpression(ReadOnlySpan<byte> expression)
    {
        if (Utf8Yaml is null || expression.IsEmpty)
        {
            return ExpressionParser.Parse(expression);
        }

        var key = ComputeContentHash(expression);

        _expressionCache ??= new();
        if (_expressionCache.TryGetValue(key, out var entry))
        {
            // Verify content match (collision guard)
            if (Utf8Yaml.AsSpan(entry.Offset, entry.Length).SequenceEqual(expression))
            {
                return entry.Result;
            }

            // Hash collision with different content — parse without caching (extremely rare)
            return ExpressionParser.Parse(expression);
        }

        var result = ExpressionParser.Parse(expression);
        var offset = (int)Unsafe.ByteOffset(
            ref MemoryMarshal.GetArrayDataReference(Utf8Yaml),
            ref MemoryMarshal.GetReference(expression));
        _expressionCache[key] = new ExpressionCacheEntry(offset, expression.Length, result);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeContentHash(ReadOnlySpan<byte> span)
    {
        return (long)XxHash64.Hash(span);
    }

    private readonly record struct ExpressionCacheEntry(int Offset, int Length, ExpressionParseResult Result);

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
    public IReadOnlyDictionary<string, RuleConfig>? Rules { get; init; }

    /// <summary>Gets the list of exclusion entries from the config.</summary>
    public IReadOnlyList<LintExclusion>? Exclusions { get; init; }

    /// <summary>Gets the fix configuration section.</summary>
    public FixConfig Fix { get; init; } = new();

    /// <summary>Gets the network configuration section.</summary>
    public NetworkConfig Network { get; init; } = new();

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
}

/// <summary>A list that extends (appends to) a rule's built-in defaults, matching the YAML <c>extend:</c> key.</summary>
public sealed record ExtendableList(IReadOnlyList<string> Extend);

/// <summary>An exclusion entry that suppresses rules for matching files/jobs.</summary>
public sealed record LintExclusion(
    string Files,
    IReadOnlyList<string> Rules,
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
    public int MaxConcurrency { get; init; } = 4;
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
