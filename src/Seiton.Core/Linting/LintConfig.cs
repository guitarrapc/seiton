using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;
using System.Text;

namespace Seiton.Core.Linting;

public sealed class LintConfig
{
    public static LintConfig Empty { get; } = new();

    public byte[]? Utf8Yaml { get; init; }

    public string? FilePath { get; init; }

    string? _sourceText;

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
    sealed record NoneRuleSpecificConfig : RuleSpecificConfig;

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
