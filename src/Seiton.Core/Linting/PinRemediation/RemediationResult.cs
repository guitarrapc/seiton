using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// Result container for network-assisted pin remediation.
/// </summary>
public sealed record RemediationResult(
    IReadOnlyList<Diagnostic> Diagnostics,
    int ResolvedCount,
    int SkippedCount,
    int FailedCount);
