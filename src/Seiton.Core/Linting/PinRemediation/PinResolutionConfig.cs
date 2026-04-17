namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// Top-level configuration for network-assisted pin remediation (Seiton_Linter_spec.md §12.3).
/// When AllowNetwork is false (the default), no network calls are made and resolvers are not invoked.
/// </summary>
public sealed record PinResolutionConfig
{
    public static PinResolutionConfig Default { get; } = new();

    /// <summary>
    /// Must be true to enable network-assisted resolution. Default: false.
    /// </summary>
    public bool AllowNetwork { get; init; } = false;

    public GitHubActionsResolutionConfig GitHubActions { get; init; } = new();
    public ImageResolutionConfig Images { get; init; } = new();

    /// <summary>
    /// When true (the default), resolution failures leave the diagnostic without a fix rather than
    /// propagating the error. When false, any resolution failure causes RemediateAsync to throw.
    /// </summary>
    public bool FailOpen { get; init; } = true;

    /// <summary>Per-request network timeout in seconds. Default: 30.</summary>
    public int RequestTimeoutSec { get; init; } = 30;

    /// <summary>Maximum number of concurrent resolution requests. Default: 4.</summary>
    public int MaxConcurrency { get; init; } = 4;
}

/// <summary>
/// Configuration for GitHub Actions SHA resolution via GitHub REST API (§12.3.2–12.3.5).
/// </summary>
public sealed record GitHubActionsResolutionConfig
{
    /// <summary>
    /// Ordered list of environment variable names to check for a GitHub API token.
    /// The first non-empty value is used. Falls back to unauthenticated if none yield a value.
    /// Default: ["SEITON_GITHUB_TOKEN", "GITHUB_TOKEN"]
    /// </summary>
    public IReadOnlyList<string> TokenEnvVars { get; init; } =
        ["SEITON_GITHUB_TOKEN", "GITHUB_TOKEN"];

    /// <summary>
    /// Optional GitHub Enterprise Server API base URL (e.g. https://ghes.example.com).
    /// When null, only github.com is used.
    /// </summary>
    public string? GhesApiUrl { get; init; } = null;

    /// <summary>
    /// When true and GhesApiUrl is set, repositories not found on GHES are retried against github.com.
    /// Default: false.
    /// </summary>
    public bool GhesFallback { get; init; } = false;

    /// <summary>
    /// Actions name/ref regex patterns to skip during SHA resolution (equivalent to pinact ignore_actions).
    /// References matching any entry are skipped (resolver returns null).
    /// </summary>
    public IReadOnlyList<IgnoreActionEntry> IgnoreActions { get; init; } = [];

    /// <summary>
    /// Branch names to never pin. Default: ["main", "master"].
    /// Pinning a branch reference to its current SHA is semantically incorrect (§12.3.5).
    /// </summary>
    public IReadOnlyList<string> ExcludeBranches { get; init; } = ["main", "master"];

    /// <summary>
    /// Minimum age in days a tag must have before it is eligible for SHA pinning.
    /// Prevents pinning to tags that were pushed very recently and may still be subject to rollback or compromise.
    /// 0 disables the age constraint entirely.
    /// Default: 14.
    /// </summary>
    public int MinAgeDays { get; init; } = 14;
}

/// <summary>
/// Configuration for OCI image digest resolution via OCI registry (§12.3.6).
/// </summary>
public sealed record ImageResolutionConfig
{
    private static readonly IReadOnlyList<string> _defaultExcludeImages = ["scratch"];
    private static readonly IReadOnlyList<string> _defaultExcludeTags = ["latest"];

    private IReadOnlyList<string> _excludeImages = _defaultExcludeImages;

    /// <summary>
    /// Image reference patterns to skip during digest resolution.
    /// "scratch" is always enforced regardless of this list (§12.3.6).
    /// Default: ["scratch"]
    /// </summary>
    public IReadOnlyList<string> ExcludeImages
    {
        get => _excludeImages;
        init => _excludeImages = EnforceScrath(value);
    }

    /// <summary>
    /// Tag patterns to skip during digest resolution. Default: ["latest"].
    /// Pinning "latest" is semantically vacuous since it drifts immediately.
    /// </summary>
    public IReadOnlyList<string> ExcludeTags { get; init; } = _defaultExcludeTags;

    /// <summary>
    /// Doublestar glob patterns for image references to skip (e.g. "ghcr.io/myorg/**").
    /// Default: empty.
    /// </summary>
    public IReadOnlyList<string> IgnoreImages { get; init; } = [];

    /// <summary>
    /// Enforces the "scratch" safety invariant: scratch must always be in ExcludeImages
    /// regardless of user configuration (matching frizbee's MergeUserConfig pattern, §12.3.6).
    /// </summary>
    private static IReadOnlyList<string> EnforceScrath(IReadOnlyList<string> values)
    {
        if (values.Contains("scratch"))
            return values;
        var list = new List<string>(values) { "scratch" };
        return list.AsReadOnly();
    }
}

/// <summary>
/// A name/ref regex pair that identifies GitHub Actions references to skip during SHA resolution.
/// Equivalent to pinact's ignore_actions entries.
/// </summary>
public sealed record IgnoreActionEntry(
    /// <summary>Regex pattern matched against "owner/repo" or "owner/repo/.github/workflows/file.yml".</summary>
    string NamePattern,
    /// <summary>Regex pattern matched against the ref portion (tag, branch, or SHA).</summary>
    string RefPattern);
