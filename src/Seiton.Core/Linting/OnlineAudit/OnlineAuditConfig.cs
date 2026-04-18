namespace Seiton.Core.Linting.OnlineAudit;

// OnlineAuditConfig and OnlineAuditGitHubConfig are abolished.
// Online rules are now enabled via rules.<rule-id>.enabled: true.
// Network settings come from the shared NetworkConfig (LintConfig.Network).
// Token resolution is hardcoded: SEITON_GITHUB_TOKEN → GITHUB_TOKEN.
