using Seiton.Core.Linting.Http;

namespace Seiton.Core.Linting;

/// <summary>Creates <see cref="HttpClient"/> instances suitable for bearer-authenticated GitHub / GHES REST calls.</summary>
public static class GitHubApiHttpClientFactory
{
    /// <summary>
    /// Builds a client whose handler stack disables default cross-origin redirects;
    /// only same-origin 3xx targets are followed (see <see cref="SameOriginRedirectHandler"/>).
    /// Container registries and other redirects should use a separate <see cref="HttpClient"/>.
    /// </summary>
    public static HttpClient CreateForGitHubApi()
    {
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        var redirect = new SameOriginRedirectHandler { InnerHandler = sockets };
        return new HttpClient(redirect, disposeHandler: true);
    }
}
