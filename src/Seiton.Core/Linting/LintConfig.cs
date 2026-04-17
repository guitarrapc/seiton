using Seiton.Core.Linting.OnlineAudit;
using Seiton.Core.Linting.PinRemediation;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting;

public sealed class LintConfig
{
    public static LintConfig Empty { get; } = new();

    public byte[]? Utf8Yaml { get; init; }

    public string? FilePath { get; init; }

    public IReadOnlyDictionary<string, RuleOption>? RuleOptions { get; init; }

    public IReadOnlyList<LintExclusion>? Exclusions { get; init; }

    public ExpressionContext ExprContext { get; init; } = ExpressionContext.Empty;

    public RuleSpecificAdditiveCustomization AdditiveCustomization { get; init; } = RuleSpecificAdditiveCustomization.Empty;

    /// <summary>
    /// Optional default timeout-minutes used by partial auto-fix for job-timeout-minutes-required.
    /// When null or <= 0, the rule reports diagnostics without attaching a fix.
    /// </summary>
    public int? DefaultJobTimeoutMinutesForFix { get; init; } = null;

    /// <summary>
    /// Optional network-assisted pin remediation configuration (Seiton_Linter_spec.md §12).
    /// When null or AllowNetwork is false, no network-assisted remediation is performed.
    /// </summary>
    public PinResolutionConfig? PinResolution { get; init; } = null;

    /// <summary>
    /// Optional network-assisted online audit configuration for advisory and ref checks.
    /// When null or AllowNetwork is false, no online audit is performed.
    /// </summary>
    public OnlineAuditConfig? OnlineAudit { get; init; } = null;
}

public sealed record ExpressionContext(
    IReadOnlyList<string>? EventTypes = null)
{
    public static ExpressionContext Empty { get; } = new();
}

public sealed record RuleOption(bool Enabled = true, DiagnosticSeverity? Severity = null);

public sealed record LintExclusion(
    string FilePattern,
    IReadOnlyList<string> RuleIds,
    string? JobId = null);

public sealed record RuleSpecificAdditiveCustomization(
    IReadOnlyList<string>? AdditionalDangerousEvents = null,
    IReadOnlyList<string>? AdditionalKnownHostedLabels = null,
    IReadOnlyList<string>? AdditionalPublicRegistries = null)
{
    public static RuleSpecificAdditiveCustomization Empty { get; } = new();
}
