using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Seiton.Core.Linting;

public sealed class LintConfig
{
    public static LintConfig Empty { get; } = new();

    public byte[]? Utf8Yaml { get; init; }

    public AstArena? Arena { get; init; }

    public string? FilePath { get; init; }

    private string? _sourceText;
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

    // rules section: rule-id -> RuleConfig
    public IReadOnlyDictionary<string, RuleConfig>? Rules { get; init; }

    // exclusions section
    public IReadOnlyList<LintExclusion>? Exclusions { get; init; }

    // fix section
    public FixConfig Fix { get; init; } = new();

    // network section
    public NetworkConfig Network { get; init; } = new();

    public RuleConfig? GetRuleConfig(string ruleId)
    {
        if (Rules is null || !Rules.TryGetValue(ruleId, out var config))
            return null;
        return config;
    }

    public RuleConfig? GetRuleConfig(RuleId ruleId) => GetRuleConfig(ruleId.ToId());
}

public sealed record RuleConfig
{
    // Shared keys
    public bool Enabled { get; init; } = true;
    public DiagnosticSeverity? Severity { get; init; }

    // Extend-style rule-specific options (YAML: key.extend[])
    public ExtendableList? Events { get; init; }
    public ExtendableList? KnownHostedLabels { get; init; }
    public ExtendableList? PublicRegistries { get; init; }
    public ExtendableList? UntrustedTriggers { get; init; }
    public ExtendableList? OutputCommands { get; init; }

    // Direct list rule-specific options (YAML: key[])
    public IReadOnlyList<string>? AssumeEvents { get; init; }
    public IReadOnlyList<string>? Allow { get; init; }
    public IReadOnlyList<string>? Deny { get; init; }

    // Scalar rule-specific options
    public int? MaxStepEnvSecrets { get; init; }
    public int? MaxJobSecrets { get; init; }
}

public sealed record ExtendableList(IReadOnlyList<string> Extend);

public sealed record LintExclusion(
    string Files,
    IReadOnlyList<string> Rules,
    IReadOnlyList<string>? Jobs = null);

public sealed record FixConfig
{
    /// <summary>
    /// When true, rules will build DiagnosticFix objects during Check().
    /// Defaults to false so lint-only runs skip fix construction overhead.
    /// </summary>
    public bool Enabled { get; init; } = false;

    public FixDefaultsConfig Defaults { get; init; } = new();
    public FixPinningConfig Pinning { get; init; } = new();
    public FixImagesConfig Images { get; init; } = new();
}

public sealed record FixDefaultsConfig
{
    public int? JobTimeoutMinutes { get; init; }
}

public sealed record FixPinningConfig
{
    public bool EnableNetwork { get; init; } = false;
    public int MinAgeDays { get; init; } = 14;
    public IReadOnlyList<string> ExcludeBranches { get; init; } = ["main", "master"];
    public IReadOnlyList<IgnoreActionEntry> IgnoreActions { get; init; } = [];
}

public sealed record FixImagesConfig
{
    private static readonly IReadOnlyList<string> DefaultExcludeImages = ["scratch"];
    private static readonly IReadOnlyList<string> DefaultExcludeTags = ["latest"];

    public bool EnableNetwork { get; init; } = false;

    private IReadOnlyList<string> _excludeImages = DefaultExcludeImages;

    public IReadOnlyList<string> ExcludeImages
    {
        get => _excludeImages;
        init => _excludeImages = EnforceScratch(value);
    }

    public IReadOnlyList<string> ExcludeTags { get; init; } = DefaultExcludeTags;
    public IReadOnlyList<string> IgnoreImages { get; init; } = [];

    private static IReadOnlyList<string> EnforceScratch(IReadOnlyList<string> values)
    {
        if (values.Contains("scratch"))
            return values;
        var list = new List<string>(values) { "scratch" };
        return list.AsReadOnly();
    }
}

public sealed record NetworkConfig
{
    public NetworkErrorMode OnError { get; init; } = NetworkErrorMode.Skip;
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxConcurrency { get; init; } = 4;
    public GitHubNetworkConfig GitHub { get; init; } = new();
}

public enum NetworkErrorMode { Skip, Fail }

public sealed record GitHubNetworkConfig
{
    public string? GhesApiUrl { get; init; } = null;
    public bool GhesFallback { get; init; } = false;
}
