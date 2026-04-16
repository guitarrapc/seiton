using System.Threading;
using System.Threading.Tasks;

namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// Resolves an OCI image reference to a pinned digest.
/// A null result indicates the reference was skipped by configuration.
/// </summary>
public interface IImageDigestResolver
{
    Task<string?> ResolveAsync(
        string imageRef,
        CancellationToken cancellationToken = default);
}
