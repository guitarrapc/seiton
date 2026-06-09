namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// Result of resolving an action reference to a pinned SHA.
/// </summary>
public readonly record struct ActionShaResolution(string? Sha, string? TagComment, string? SkipReason = null)
{
    public static ActionShaResolution Resolved(string sha, string tagComment)
        => new(sha, tagComment, null);

    public static ActionShaResolution Skipped(string reason)
        => new(null, null, reason);
}
