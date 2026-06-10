namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// Result of resolving an OCI image reference to a pinned digest.
/// </summary>
public readonly record struct ImageDigestResolution(string? Digest, string? SkipReason = null)
{
    /// <summary>Creates a successful resolution with the resolved digest.</summary>
    public static ImageDigestResolution Resolved(string digest)
        => new(digest, null);

    /// <summary>Creates a configuration-driven skip with a user-visible reason.</summary>
    public static ImageDigestResolution Skipped(string reason)
        => new(null, reason);
}
