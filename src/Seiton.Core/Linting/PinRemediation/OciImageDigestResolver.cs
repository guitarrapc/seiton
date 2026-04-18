using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Seiton.Core.Linting.PinRemediation;

public sealed class OciImageDigestResolver : IImageDigestResolver
{
    static readonly string[] ManifestAcceptHeaders =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    readonly IHttpClientFactory _httpClientFactory;
    readonly FixImagesConfig _config;
    readonly string? _dockerConfigPath;
    readonly ConcurrentDictionary<string, string> _successCache = new(StringComparer.Ordinal);
    readonly string[] _normalizedExcludeImages;
    readonly string[] _normalizedExcludeTags;
    readonly string[] _normalizedIgnoreImages;
    volatile DockerAuthConfig? _dockerAuthConfig;

    public OciImageDigestResolver(IHttpClientFactory httpClientFactory, FixImagesConfig config)
        : this(httpClientFactory, config, dockerConfigPath: null)
    {
    }

    internal OciImageDigestResolver(IHttpClientFactory httpClientFactory, FixImagesConfig config, string? dockerConfigPath)
    {
        _httpClientFactory = httpClientFactory;
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

        using var request = new HttpRequestMessage(HttpMethod.Head, BuildManifestUri(parsed));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Seiton", "1.0"));
        for (var i = 0; i < ManifestAcceptHeaders.Length; i++)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ManifestAcceptHeaders[i]));
        }

        var authHeader = ResolveAuthorizationHeader(parsed.RegistryHost);
        if (authHeader is not null)
        {
            request.Headers.Authorization = authHeader;
        }

        var client = _httpClientFactory.CreateClient(nameof(OciImageDigestResolver));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to resolve OCI digest for '{imageRef}' via '{request.RequestUri}' (status {(int)response.StatusCode}).");
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

        _successCache.TryAdd(parsed.CacheKey, digest!);
        return digest;
    }

    bool ShouldSkip(ParsedImageReference parsed)
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

    AuthenticationHeaderValue? ResolveAuthorizationHeader(string registryHost)
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

    static Uri BuildManifestUri(ParsedImageReference parsed)
    {
        return new Uri($"https://{parsed.RegistryHost}/v2/{parsed.RepositoryPath}/manifests/{Uri.EscapeDataString(parsed.Reference)}");
    }

    static string[] NormalizeEntries(IReadOnlyList<string> values)
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

    static string NormalizeValue(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    static bool ContainsExact(string[] values, string target)
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

    static bool TryParseImageReference(string imageRef, out ParsedImageReference parsed)
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
        var hasExplicitRegistry = firstSegment.Contains('.') || firstSegment.Contains(':') || string.Equals(firstSegment, "localhost", StringComparison.OrdinalIgnoreCase);

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

    static bool IsSha256Digest(string? digest)
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

    static DockerAuthConfig LoadDockerAuthConfig(string? dockerConfigPath)
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

    static string? ResolveDockerConfigPath(string? dockerConfigPath)
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

    static string NormalizeRegistryKey(string value)
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

    static bool GlobMatch(string pattern, string path)
    {
        var normalizedPattern = pattern.Replace('\\', '/');
        var normalizedPath = path.Replace('\\', '/');
        var cache = new Dictionary<(int PatternIndex, int PathIndex), bool>();
        return GlobMatchCore(normalizedPattern, normalizedPath, 0, 0, cache);
    }

    static bool GlobMatchCore(
        string pattern,
        string path,
        int patternIndex,
        int pathIndex,
        Dictionary<(int PatternIndex, int PathIndex), bool> cache)
    {
        if (cache.TryGetValue((patternIndex, pathIndex), out var cached))
        {
            return cached;
        }

        bool result;
        if (patternIndex == pattern.Length)
        {
            result = pathIndex == path.Length;
        }
        else if (pattern[patternIndex] == '*')
        {
            var isDoubleStar = patternIndex + 1 < pattern.Length && pattern[patternIndex + 1] == '*';
            if (isDoubleStar)
            {
                patternIndex += 2;
                result = GlobMatchCore(pattern, path, patternIndex, pathIndex, cache);
                if (!result)
                {
                    for (var cursor = pathIndex; cursor < path.Length; cursor++)
                    {
                        if (GlobMatchCore(pattern, path, patternIndex, cursor + 1, cache))
                        {
                            result = true;
                            break;
                        }
                    }
                }
            }
            else
            {
                result = GlobMatchCore(pattern, path, patternIndex + 1, pathIndex, cache);
                if (!result)
                {
                    for (var cursor = pathIndex; cursor < path.Length && path[cursor] != '/'; cursor++)
                    {
                        if (GlobMatchCore(pattern, path, patternIndex + 1, cursor + 1, cache))
                        {
                            result = true;
                            break;
                        }
                    }
                }
            }
        }
        else if (pathIndex < path.Length && (pattern[patternIndex] == '?' || pattern[patternIndex] == path[pathIndex]))
        {
            result = GlobMatchCore(pattern, path, patternIndex + 1, pathIndex + 1, cache);
        }
        else
        {
            result = false;
        }

        cache[(patternIndex, pathIndex)] = result;
        return result;
    }

    readonly record struct ParsedImageReference(
        string RegistryHost,
        string RepositoryPath,
        string MatchName,
        string Reference,
        string CacheKey,
        bool AlreadyPinned);

    sealed class DockerAuthConfig(IReadOnlyDictionary<string, string> auths)
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
