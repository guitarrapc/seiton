namespace Seiton.Core.Linting;

/// <summary>
/// Validates and normalizes <c>network.github.ghes-api-url</c>. GitHub API clients attach bearer tokens,
/// so only absolute HTTPS URLs without embedded credentials are accepted.
/// </summary>
internal static class GitHubEnterpriseApiBase
{
    /// <summary>Validates a non-empty trimmed value for configuration loading.</summary>
    public static bool TryValidateForConfig(string trimmedNonEmpty, out string canonicalUri, out string diagnosticMessage)
    {
        canonicalUri = trimmedNonEmpty;
        diagnosticMessage = string.Empty;

        if (!TryValidate(trimmedNonEmpty, out var uri, out diagnosticMessage))
        {
            canonicalUri = string.Empty;
            return false;
        }

        canonicalUri = uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// Normalizes stored config URL for outbound GitHub REST requests (ensures trailing '/' on path for relative merges).
    /// </summary>
    /// <exception cref="InvalidOperationException">URL violates GHES HTTPS policy (caller bypassed Validate).</exception>
    public static Uri ToRequestBaseUri(string ghesApiUrlFromConfig)
    {
        var trimmed = ghesApiUrlFromConfig.Trim();
        if (!TryValidate(trimmed, out var baseUri, out var diagnostic))
        {
            throw new InvalidOperationException($"Invalid GHES API base URL: {diagnostic}");
        }

        var builder = new UriBuilder(baseUri);
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static bool TryValidate(string value, out Uri uri, out string diagnosticMessage)
    {
        uri = null!;
        diagnosticMessage = string.Empty;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            diagnosticMessage = "network.github.ghes-api-url must be an absolute https URL";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            diagnosticMessage = $"network.github.ghes-api-url must use the https scheme (was '{parsed.Scheme}')";
            return false;
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            diagnosticMessage = "network.github.ghes-api-url must include a host name";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            diagnosticMessage = "network.github.ghes-api-url must not include user credentials";
            return false;
        }

        uri = parsed;
        return true;
    }
}

