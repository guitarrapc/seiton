namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// Resolves a GitHub Actions or reusable-workflow reference to a pinned commit SHA.
/// A null SHA indicates the reference was skipped.
/// </summary>
public interface IActionShaResolver
{
    /// <summary>Resolves the given action reference to a commit SHA and tag comment.</summary>
    public Task<ActionShaResolution> ResolveAsync(
        string owner,
        string repo,
        string refStr,
        CancellationToken cancellationToken = default);
}
