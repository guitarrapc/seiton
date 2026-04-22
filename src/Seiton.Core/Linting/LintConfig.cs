using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

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
    /// Returns the decoded UTF-8 source text, lazily initialized on first access.
    /// Multiple rules requesting source text will share the same decoded string.
    /// </summary>
    public string? GetSourceText()
    {
        if (Utf8Yaml is null)
        {
            return null;
        }

        return _sourceText ??= Encoding.UTF8.GetString(Utf8Yaml);
    }

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
}

public sealed record RuleConfig
{
    // Shared keys (formerly RuleOption)
    public bool Enabled { get; init; } = true;
    public DiagnosticSeverity? Severity { get; init; }

    // Discriminated-union style, typed rule-specific payload.
    // This is the authoritative per-rule customization shape after normalization.
    public RuleSpecificConfig Specific { get; init; } = RuleSpecificConfig.None;
}

public abstract record RuleSpecificConfig
{
    private sealed record NoneRuleSpecificConfig : RuleSpecificConfig;

    public static RuleSpecificConfig None { get; } = new NoneRuleSpecificConfig();
}

public sealed record DangerousTriggersSpecificConfig(IReadOnlyList<string> Events) : RuleSpecificConfig;

public sealed record RunnerLabelSpecificConfig(IReadOnlyList<string> KnownHostedLabels) : RuleSpecificConfig;

public sealed record CredentialsSpecificConfig(IReadOnlyList<string> PublicRegistries) : RuleSpecificConfig;

public sealed record UntrustedTriggersSpecificConfig(IReadOnlyList<string> UntrustedTriggers) : RuleSpecificConfig;

public sealed record UnredactedSecretsSpecificConfig(IReadOnlyList<string> OutputCommands) : RuleSpecificConfig;

public sealed record ExprUndefinedVarSpecificConfig(IReadOnlyList<string> AssumeEvents) : RuleSpecificConfig;

public sealed record ForbiddenUsesSpecificConfig(IReadOnlyList<string>? Allow, IReadOnlyList<string>? Deny) : RuleSpecificConfig;

public sealed record OverprovisionedSecretsSpecificConfig(int MaxStepEnvSecrets, int MaxJobSecrets) : RuleSpecificConfig;

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
