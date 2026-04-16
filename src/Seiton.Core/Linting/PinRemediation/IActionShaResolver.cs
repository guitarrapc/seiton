using System.Threading;
using System.Threading.Tasks;

namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// Resolves a GitHub Actions or reusable-workflow reference to a pinned commit SHA.
/// A null tuple indicates the reference was skipped by configuration.
/// </summary>
public interface IActionShaResolver
{
    Task<(string? Sha, string? TagComment)> ResolveAsync(
        string owner,
        string repo,
        string refStr,
        CancellationToken cancellationToken = default);
}
