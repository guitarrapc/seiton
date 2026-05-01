using System.Net;

namespace Seiton.Core.Linting.Http;

/// <summary>
/// Follows 3xx redirects only when the Location target shares the same scheme, host, and port as the prior request.
/// Prevents bearer tokens from being replayed to a different origin after a hostile redirect.
/// </summary>
internal sealed class SameOriginRedirectHandler : DelegatingHandler
{
    private const int MaxRedirects = 10;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        HttpRequestMessage? followOwned = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var redirects = 0;
            while (IsRedirectStatusCode(response.StatusCode))
            {
                if (++redirects > MaxRedirects || request.RequestUri is null)
                {
                    return response;
                }

                var baseUriForRelative = response.RequestMessage?.RequestUri ?? request.RequestUri;

                var location = response.Headers.Location;
                if (location is null || string.IsNullOrEmpty(location.OriginalString))
                {
                    return response;
                }

                var nextUri = location.IsAbsoluteUri ? location : new Uri(baseUriForRelative, location);

                if (!IsSameOrigin(request.RequestUri, nextUri))
                {
                    return response;
                }

                followOwned?.Dispose();

                followOwned = CloneFollowRequest(request, nextUri);

                var previous = response;
                response = await base.SendAsync(followOwned, cancellationToken).ConfigureAwait(false);
                previous.Dispose();
            }

            return response;
        }
        catch
        {
            response?.Dispose();
            throw;
        }
        finally
        {
            followOwned?.Dispose();
        }
    }

    private static HttpRequestMessage CloneFollowRequest(HttpRequestMessage template, Uri requestUri)
    {
        // GHES / github.com resolver paths use GET; redirect responses do not mutate method for these callers.
        var clone = new HttpRequestMessage(HttpMethod.Get, requestUri)
        {
            Version = template.Version,
        };

        foreach (var header in template.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static bool IsSameOrigin(Uri originalRequestUri, Uri nextUri) =>
        string.Equals(originalRequestUri.Scheme, nextUri.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(originalRequestUri.Host, nextUri.Host, StringComparison.OrdinalIgnoreCase)
        && originalRequestUri.Port == nextUri.Port;

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}
