using Seiton.Core.Linting.PinRemediation;

namespace Seiton.Core.Linting.OnlineAudit;

public sealed record OnlineAuditConfig
{
    public static OnlineAuditConfig Default { get; } = new();

    public bool AllowNetwork { get; init; } = false;

    public OnlineAuditGitHubConfig GitHubActions { get; init; } = new();

    public bool FailOpen { get; init; } = true;

    public int RequestTimeoutSec { get; init; } = 30;

    public int MaxConcurrency { get; init; } = 4;
}

public sealed record OnlineAuditGitHubConfig
{
    public IReadOnlyList<string> TokenEnvVars { get; init; } =
        ["SEITON_GITHUB_TOKEN", "GITHUB_TOKEN"];

    public string? GhesApiUrl { get; init; } = null;

    public bool GhesFallback { get; init; } = false;

    public IReadOnlyList<IgnoreActionEntry> IgnoreActions { get; init; } = [];
}
