namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// Resolves a GitHub Actions or reusable-workflow reference to a pinned commit SHA.
/// A null tuple indicates the reference was skipped by configuration.
/// </summary>
public interface IActionShaResolver
{
    /// <summary>Resolves the given action reference to a commit SHA and tag comment. Returns nulls if skipped by configuration.</summary>
    Task<(string? Sha, string? TagComment)> ResolveAsync(
        string owner,
        string repo,
        string refStr,
        CancellationToken cancellationToken = default);
}
