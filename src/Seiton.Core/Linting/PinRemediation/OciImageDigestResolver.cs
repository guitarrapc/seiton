using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using static Seiton.Core.Linting.ActionRefHelpers;

namespace Seiton.Core.Linting.PinRemediation;

public sealed class OciImageDigestResolver : IImageDigestResolver
{
    private static readonly string[] ManifestAcceptHeaders =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    private readonly HttpClient _httpClient;
    private readonly FixImagesConfig _config;
    private readonly string? _dockerConfigPath;
    private readonly ConcurrentDictionary<string, string> _successCache = new(StringComparer.Ordinal);
    private readonly string[] _normalizedExcludeImages;
    private readonly string[] _normalizedExcludeTags;
    private readonly string[] _normalizedIgnoreImages;
    private volatile DockerAuthConfig? _dockerAuthConfig;

    public OciImageDigestResolver(HttpClient httpClient, FixImagesConfig config)
        : this(httpClient, config, dockerConfigPath: null)
    {
    }

    internal OciImageDigestResolver(HttpClient httpClient, FixImagesConfig config, string? dockerConfigPath)
    {
        _httpClient = httpClient;
        _config = config;
        _dockerConfigPath = dockerConfigPath;
        _normalizedExcludeImages = NormalizeEntries(config.ExcludeImages);
        _normalizedExcludeTags = NormalizeEntries(config.ExcludeTags);
        _normalizedIgnoreImages = NormalizeEntries(config.IgnoreImages);
    }

    public async Task<string?> ResolveAsync(string imageRef, CancellationToken cancellationToken = default)
    {
        if (!TryParseImageReference(imageRef, out var parsed))
        {
            throw new InvalidOperationException($"Failed to parse OCI image reference '{imageRef}'.");
        }

        if (parsed.AlreadyPinned)
        {
            return null;
        }

        if (ShouldSkip(parsed))
        {
            return null;
        }

        if (_successCache.TryGetValue(parsed.CacheKey, out var cachedDigest))
        {
            return cachedDigest;
        }

        var client = _httpClient;
        var manifestUri = BuildManifestUri(parsed);
        var storedAuth = ResolveAuthorizationHeader(parsed.RegistryHost);

        var digest = await ResolveDigestAsync(client, manifestUri, storedAuth, imageRef, cancellationToken);
        if (digest is not null)
        {
            _successCache.TryAdd(parsed.CacheKey, digest);
        }

        return digest;
    }

    private async Task<string?> ResolveDigestAsync(
        HttpClient client,
        Uri manifestUri,
        AuthenticationHeaderValue? storedAuth,
        string imageRef,
        CancellationToken cancellationToken)
    {
        using var initialResponse = await SendHeadRequestAsync(client, manifestUri, storedAuth, cancellationToken);

        // Image does not exist — return null instead of throwing.
        if (initialResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        // OCI bearer token challenge flow (RFC 6750 / Docker auth spec).
        // Triggered when the registry returns 401 and we have no stored credentials.
        // This covers anonymous access to Docker Hub official images, which requires
        // obtaining a short-lived bearer token from auth.docker.io before the HEAD retry.
        if (initialResponse.StatusCode == HttpStatusCode.Unauthorized && storedAuth is null)
        {
            var bearerToken = await TryAcquireBearerTokenAsync(client, initialResponse, cancellationToken);
            if (bearerToken is not null)
            {
                using var authResponse = await SendHeadRequestAsync(
                    client, manifestUri,
                    new AuthenticationHeaderValue("Bearer", bearerToken),
                    cancellationToken);

                // Image may not exist even after successful auth.
                if (authResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                return ExtractDigest(authResponse, imageRef, manifestUri);
            }
        }

        return ExtractDigest(initialResponse, imageRef, manifestUri);
    }

    private async Task<HttpResponseMessage> SendHeadRequestAsync(
        HttpClient client,
        Uri manifestUri,
        AuthenticationHeaderValue? auth,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, manifestUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Seiton", "1.0"));
        for (var i = 0; i < ManifestAcceptHeaders.Length; i++)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ManifestAcceptHeaders[i]));
        }

        if (auth is not null)
        {
            request.Headers.Authorization = auth;
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static string ExtractDigest(HttpResponseMessage response, string imageRef, Uri requestUri)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to resolve OCI digest for '{imageRef}' via '{requestUri}' (status {(int)response.StatusCode}).");
        }

        if (!response.Headers.TryGetValues("Docker-Content-Digest", out var digestValues))
        {
            throw new InvalidOperationException(
                $"Registry response for '{imageRef}' did not include Docker-Content-Digest header.");
        }

        var digest = digestValues.FirstOrDefault();
        if (!IsSha256Digest(digest))
        {
            throw new InvalidOperationException(
                $"Registry response for '{imageRef}' returned invalid digest '{digest ?? string.Empty}'.");
        }

        return digest!;
    }

    // Parses the WWW-Authenticate: Bearer header and fetches a short-lived token
    // from the registry's auth endpoint. Returns null when the challenge cannot be
    // fulfilled (missing realm, non-HTTPS endpoint, or token endpoint failure).
    private static async Task<string?> TryAcquireBearerTokenAsync(
        HttpClient client,
        HttpResponseMessage challengeResponse,
        CancellationToken cancellationToken)
    {
        var wwwAuth = challengeResponse.Headers.WwwAuthenticate
            .FirstOrDefault(h => string.Equals(h.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));
        if (wwwAuth?.Parameter is null)
        {
            return null;
        }

        if (!TryParseBearerChallenge(wwwAuth.Parameter, out var realm, out var service, out var scope))
        {
            return null;
        }

        // Security: only follow HTTPS auth endpoints to prevent credential exposure.
        if (!Uri.TryCreate(realm, UriKind.Absolute, out var realmUri)
            || !string.Equals(realmUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tokenUri = BuildTokenUri(realmUri, service, scope);
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, tokenUri);
        tokenRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("Seiton", "1.0"));

        using var tokenResponse = await client.SendAsync(
            tokenRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await tokenResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        // Docker Hub returns both "access_token" and "token"; prefer "access_token" per OAuth2 spec.
        if (doc.RootElement.TryGetProperty("access_token", out var accessToken))
        {
            return accessToken.GetString();
        }

        if (doc.RootElement.TryGetProperty("token", out var tokenProp))
        {
            return tokenProp.GetString();
        }

        return null;
    }

    private static bool TryParseBearerChallenge(string parameter, out string? realm, out string? service, out string? scope)
    {
        realm = null;
        service = null;
        scope = null;

        var remaining = parameter.AsSpan();
        while (remaining.Length > 0)
        {
            remaining = remaining.TrimStart();
            var eq = remaining.IndexOf('=');
            if (eq < 0)
            {
                break;
            }

            var key = remaining[..eq].TrimEnd();
            remaining = remaining[(eq + 1)..];

            string? value;
            if (remaining.Length > 0 && remaining[0] == '"')
            {
                remaining = remaining[1..];
                var close = remaining.IndexOf('"');
                if (close < 0)
                {
                    break;
                }

                value = remaining[..close].ToString();
                remaining = remaining[(close + 1)..];
                if (remaining.Length > 0 && remaining[0] == ',')
                {
                    remaining = remaining[1..];
                }
            }
            else
            {
                var comma = remaining.IndexOf(',');
                if (comma < 0)
                {
                    value = remaining.ToString();
                    remaining = [];
                }
                else
                {
                    value = remaining[..comma].ToString();
                    remaining = remaining[(comma + 1)..];
                }
            }

            if (key.Equals("realm", StringComparison.OrdinalIgnoreCase))
            {
                realm = value;
            }
            else if (key.Equals("service", StringComparison.OrdinalIgnoreCase))
            {
                service = value;
            }
            else if (key.Equals("scope", StringComparison.OrdinalIgnoreCase))
            {
                scope = value;
            }
        }

        return realm is not null;
    }

    private static Uri BuildTokenUri(Uri realm, string? service, string? scope)
    {
        var hasService = !string.IsNullOrEmpty(service);
        var hasScope = !string.IsNullOrEmpty(scope);
        if (!hasService && !hasScope)
        {
            return realm;
        }

        var realmStr = realm.ToString();
        var sep = realmStr.Contains('?') ? "&" : "?";
        var query = (hasService, hasScope) switch
        {
            (true, true) => $"{sep}service={Uri.EscapeDataString(service!)}&scope={Uri.EscapeDataString(scope!)}",
            (true, false) => $"{sep}service={Uri.EscapeDataString(service!)}",
            _ => $"{sep}scope={Uri.EscapeDataString(scope!)}",
        };

        return new Uri(realmStr + query);
    }

    private bool ShouldSkip(ParsedImageReference parsed)
    {
        if (ContainsExact(_normalizedExcludeImages, parsed.MatchName)
            || ContainsExact(_normalizedExcludeImages, parsed.RepositoryPath))
        {
            return true;
        }

        if (ContainsExact(_normalizedExcludeTags, parsed.Reference))
        {
            return true;
        }

        for (var i = 0; i < _normalizedIgnoreImages.Length; i++)
        {
            if (GlobMatch(_normalizedIgnoreImages[i], parsed.MatchName)
                || GlobMatch(_normalizedIgnoreImages[i], parsed.RepositoryPath))
            {
                return true;
            }
        }

        return false;
    }

    private AuthenticationHeaderValue? ResolveAuthorizationHeader(string registryHost)
    {
        var dockerAuthConfig = _dockerAuthConfig;
        if (dockerAuthConfig is null)
        {
            dockerAuthConfig = LoadDockerAuthConfig(_dockerConfigPath);
            _dockerAuthConfig = dockerAuthConfig;
        }

        if (!dockerAuthConfig.TryGetAuthorization(registryHost, out var authValue))
        {
            return null;
        }

        return new AuthenticationHeaderValue("Basic", authValue);
    }

    private static Uri BuildManifestUri(ParsedImageReference parsed)
    {
        return new Uri($"https://{parsed.RegistryHost}/v2/{parsed.RepositoryPath}/manifests/{Uri.EscapeDataString(parsed.Reference)}");
    }

    private static string[] NormalizeEntries(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var list = new List<string>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var value = NormalizeValue(values[i]);
            if (value.Length > 0)
            {
                list.Add(value);
            }
        }

        return [.. list];
    }

    private static string NormalizeValue(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static bool ContainsExact(string[] values, string target)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (string.Equals(values[i], target, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseImageReference(string imageRef, out ParsedImageReference parsed)
    {
        parsed = default;

        if (string.IsNullOrWhiteSpace(imageRef))
        {
            return false;
        }

        var normalized = imageRef.StartsWith("docker://", StringComparison.OrdinalIgnoreCase)
            ? imageRef["docker://".Length..]
            : imageRef;
        normalized = normalized.Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        var at = normalized.LastIndexOf('@');
        if (at >= 0)
        {
            var digest = normalized[(at + 1)..];
            if (IsSha256Digest(digest))
            {
                parsed = new ParsedImageReference(
                    RegistryHost: string.Empty,
                    RepositoryPath: string.Empty,
                    MatchName: string.Empty,
                    Reference: string.Empty,
                    CacheKey: string.Empty,
                    AlreadyPinned: true);
                return true;
            }
        }

        var slash = normalized.IndexOf('/');
        var firstSegment = slash >= 0 ? normalized[..slash] : normalized;
        var hasExplicitRegistry = slash >= 0
            && (firstSegment.Contains('.') || firstSegment.Contains(':') || string.Equals(firstSegment, "localhost", StringComparison.OrdinalIgnoreCase));

        string registryHost;
        string repositoryPath;
        if (hasExplicitRegistry)
        {
            registryHost = firstSegment;
            repositoryPath = slash >= 0 ? normalized[(slash + 1)..] : string.Empty;
        }
        else
        {
            registryHost = "registry-1.docker.io";
            repositoryPath = normalized;
        }

        if (repositoryPath.Length == 0)
        {
            return false;
        }

        var lastSlash = repositoryPath.LastIndexOf('/');
        var lastColon = repositoryPath.LastIndexOf(':');
        string reference;
        string namedReference;
        if (lastColon > lastSlash)
        {
            reference = repositoryPath[(lastColon + 1)..];
            namedReference = repositoryPath[..lastColon];
        }
        else
        {
            reference = "latest";
            namedReference = repositoryPath;
        }

        if (!hasExplicitRegistry && !namedReference.Contains('/'))
        {
            namedReference = "library/" + namedReference;
        }

        var normalizedNamedReference = NormalizeValue(namedReference);
        var normalizedReference = NormalizeValue(reference);
        var matchName = hasExplicitRegistry
            ? NormalizeValue(registryHost) + "/" + normalizedNamedReference
            : NormalizeValue(namedReference);

        parsed = new ParsedImageReference(
            RegistryHost: NormalizeValue(registryHost),
            RepositoryPath: normalizedNamedReference,
            MatchName: matchName,
            Reference: normalizedReference,
            CacheKey: NormalizeValue(registryHost) + "/" + normalizedNamedReference + ":" + normalizedReference,
            AlreadyPinned: false);
        return true;
    }

    private static bool IsSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hash = digest["sha256:".Length..];
        if (hash.Length != 64)
        {
            return false;
        }

        for (var i = 0; i < hash.Length; i++)
        {
            var c = hash[i];
            var isDigit = c is >= '0' and <= '9';
            var isLowerHex = c is >= 'a' and <= 'f';
            var isUpperHex = c is >= 'A' and <= 'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }

    private static DockerAuthConfig LoadDockerAuthConfig(string? dockerConfigPath)
    {
        var configPath = ResolveDockerConfigPath(dockerConfigPath);
        if (configPath is null || !File.Exists(configPath))
        {
            return DockerAuthConfig.Empty;
        }

        using var stream = File.OpenRead(configPath);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("auths", out var authsElement)
            || authsElement.ValueKind != JsonValueKind.Object)
        {
            return DockerAuthConfig.Empty;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var authEntry in authsElement.EnumerateObject())
        {
            if (authEntry.Value.ValueKind != JsonValueKind.Object
                || !authEntry.Value.TryGetProperty("auth", out var authValueElement))
            {
                continue;
            }

            var authValue = authValueElement.GetString();
            if (string.IsNullOrWhiteSpace(authValue))
            {
                continue;
            }

            var host = NormalizeRegistryKey(authEntry.Name);
            if (host.Length == 0)
            {
                continue;
            }

            map[host] = authValue;
        }

        return map.Count == 0 ? DockerAuthConfig.Empty : new DockerAuthConfig(map);
    }

    private static string? ResolveDockerConfigPath(string? dockerConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(dockerConfigPath))
        {
            return dockerConfigPath;
        }

        var dockerConfigDir = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
        if (!string.IsNullOrWhiteSpace(dockerConfigDir))
        {
            return Path.Combine(dockerConfigDir, "config.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, ".docker", "config.json");
        }

        return null;
    }

    private static string NormalizeRegistryKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return NormalizeValue(uri.Host);
        }

        var slash = trimmed.IndexOf('/');
        var host = slash >= 0 ? trimmed[..slash] : trimmed;
        return NormalizeValue(host);
    }
    private readonly record struct ParsedImageReference(
        string RegistryHost,
        string RepositoryPath,
        string MatchName,
        string Reference,
        string CacheKey,
        bool AlreadyPinned);

    private sealed class DockerAuthConfig(IReadOnlyDictionary<string, string> auths)
    {
        public static DockerAuthConfig Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

        public bool TryGetAuthorization(string registryHost, out string authValue)
        {
            if (auths.TryGetValue(registryHost, out var directValue) && !string.IsNullOrEmpty(directValue))
            {
                authValue = directValue;
                return true;
            }

            if (string.Equals(registryHost, "registry-1.docker.io", StringComparison.Ordinal)
                && auths.TryGetValue("index.docker.io", out var dockerHubValue)
                && !string.IsNullOrEmpty(dockerHubValue))
            {
                authValue = dockerHubValue;
                return true;
            }

            authValue = string.Empty;
            return false;
        }
    }
}
