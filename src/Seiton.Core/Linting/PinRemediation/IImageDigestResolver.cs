namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// Resolves an OCI image reference to a pinned digest.
/// </summary>
public interface IImageDigestResolver
{
    /// <summary>
    /// Resolves the given image reference to an OCI digest.
    /// <see cref="ImageDigestResolution.SkipReason"/> is set when excluded by configuration.
    /// <see cref="ImageDigestResolution.Digest"/> is null when skipped, not found (404), or already pinned.
    /// </summary>
    public Task<ImageDigestResolution> ResolveAsync(
        string imageRef,
        CancellationToken cancellationToken = default);
}
