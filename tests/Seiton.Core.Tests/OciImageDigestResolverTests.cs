using System.Net;
using Seiton.Core.Linting;
using Seiton.Core.Linting.PinRemediation;

namespace Seiton.Core.Tests;

public sealed class OciImageDigestResolverTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    public async Task ResolveAsync_ReturnsDigest_ForExplicitRegistryTag()
    {
        var handler = new StubHttpMessageHandler();
        handler.AddHead("https://ghcr.io/v2/astral-sh/uv/manifests/0.5.4", Digest);

        var resolver = CreateResolver(handler);

        var digest = await resolver.ResolveAsync("ghcr.io/astral-sh/uv:0.5.4");

        await Assert.That(digest).IsEqualTo(Digest);
        await Assert.That(handler.RequestedUris).Contains("https://ghcr.io/v2/astral-sh/uv/manifests/0.5.4");
    }

    [Test]
    public async Task ResolveAsync_ReturnsNull_ForScratch_AndLatestDefaults()
    {
        var handler = new StubHttpMessageHandler();
        var resolver = CreateResolver(handler);

        var scratch = await resolver.ResolveAsync("scratch");
        var latest = await resolver.ResolveAsync("docker://ghcr.io/astral-sh/uv:latest");
        var implicitLatest = await resolver.ResolveAsync("ghcr.io/astral-sh/uv");

        await Assert.That(scratch).IsNull();
        await Assert.That(latest).IsNull();
        await Assert.That(implicitLatest).IsNull();
        await Assert.That(handler.RequestedUris).IsEmpty();
    }

    [Test]
    public async Task ResolveAsync_ReturnsNull_WhenSkipRulesMatch()
    {
        var handler = new StubHttpMessageHandler();
        var resolver = CreateResolver(
            handler,
            new FixImagesConfig
            {
                ExcludeImages = ["ghcr.io/internal/runner"],
                ExcludeTags = ["edge"],
                IgnoreImages = ["ghcr.io/myorg/**"],
            });

        var excludedImage = await resolver.ResolveAsync("ghcr.io/internal/runner:1.0.0");
        var excludedTag = await resolver.ResolveAsync("ghcr.io/astral-sh/uv:edge");
        var ignoredImage = await resolver.ResolveAsync("ghcr.io/myorg/tooling/ci:1.2.3");

        await Assert.That(excludedImage).IsNull();
        await Assert.That(excludedTag).IsNull();
        await Assert.That(ignoredImage).IsNull();
        await Assert.That(handler.RequestedUris).IsEmpty();
    }

    [Test]
    public async Task ResolveAsync_UsesDockerAuths_WhenRegistryCredentialsExist()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dockerConfigPath = Path.Combine(tempDir, "config.json");
            await File.WriteAllTextAsync(
                dockerConfigPath,
                """
                {
                  "auths": {
                    "https://ghcr.io": {
                      "auth": "dXNlcjp0b2tlbg=="
                    }
                  }
                }
                """);

            var handler = new StubHttpMessageHandler();
            handler.AddHead("https://ghcr.io/v2/astral-sh/uv/manifests/0.5.4", Digest);
            var resolver = CreateResolver(handler, dockerConfigPath: dockerConfigPath);

            var digest = await resolver.ResolveAsync("ghcr.io/astral-sh/uv:0.5.4");

            await Assert.That(digest).IsEqualTo(Digest);
            await Assert.That(handler.LastAuthorizationScheme).IsEqualTo("Basic");
            await Assert.That(handler.LastAuthorizationParameter).IsEqualTo("dXNlcjp0b2tlbg==");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task ResolveAsync_CachesSuccessfulResolution()
    {
        var handler = new StubHttpMessageHandler();
        handler.AddHead("https://ghcr.io/v2/astral-sh/uv/manifests/0.5.4", Digest);
        var resolver = CreateResolver(handler);

        var first = await resolver.ResolveAsync("docker://ghcr.io/astral-sh/uv:0.5.4");
        var second = await resolver.ResolveAsync("ghcr.io/astral-sh/uv:0.5.4");

        await Assert.That(first).IsEqualTo(second);
        await Assert.That(handler.RequestedUris.Count(uri => uri == "https://ghcr.io/v2/astral-sh/uv/manifests/0.5.4"))
            .IsEqualTo(1);
    }

    [Test]
    public async Task ResolveAsync_DockerHub_AnonymousImage_UsesBearerTokenFlow()
    {
        // Docker Hub's registry-1.docker.io returns 401 + WWW-Authenticate: Bearer for anonymous
        // requests. The resolver must request a short-lived token from the auth endpoint and
        // retry the HEAD with that token.
        const string manifestUri = "https://registry-1.docker.io/v2/library/node/manifests/20.11.1";
        const string tokenEndpoint = "https://auth.docker.io/token";
        const string bearerToken = "testbearertoken_abcxyz";

        var handler = new StubHttpMessageHandler();
        handler.AddBearerChallenge(
            manifestUri,
            realm: tokenEndpoint,
            service: "registry.docker.io",
            scope: "repository:library/node:pull");
        handler.AddTokenEndpoint(tokenEndpoint, bearerToken);
        handler.AddHeadWithBearer(manifestUri, bearerToken, Digest);

        var resolver = CreateResolver(handler);
        var digest = await resolver.ResolveAsync("node:20.11.1");

        await Assert.That(digest).IsEqualTo(Digest);
        await Assert.That(handler.RequestedUris.Count(u => u == manifestUri)).IsEqualTo(2); // 401 + retry
        await Assert.That(handler.RequestedUris.Any(u => u.StartsWith(tokenEndpoint))).IsTrue();
    }

    [Test]
    public async Task ResolveAsync_ImageNotFound_ReturnsNull()
    {
        // A 404 response means the image does not exist — return null rather than throwing.
        var handler = new StubHttpMessageHandler(); // unregistered URIs return 404 by default
        var resolver = CreateResolver(handler);

        var digest = await resolver.ResolveAsync("ghcr.io/myorg/nonexistent:1.0.0");

        await Assert.That(digest).IsNull();
    }

    [Test]
    public async Task ResolveAsync_ImageNotFoundAfterBearerTokenAuth_ReturnsNull()
    {
        // The registry requires auth (401 first), but after obtaining a bearer token the
        // manifest still returns 404. Should return null rather than throwing.
        const string manifestUri = "https://registry-1.docker.io/v2/library/ghost/manifests/4.0.0";
        const string tokenEndpoint = "https://auth.docker.io/token";
        const string bearerToken = "ghosttoken";

        var handler = new StubHttpMessageHandler();
        handler.AddBearerChallenge(
            manifestUri,
            realm: tokenEndpoint,
            service: "registry.docker.io",
            scope: "repository:library/ghost:pull");
        handler.AddTokenEndpoint(tokenEndpoint, bearerToken);
        // No AddHeadWithBearer — handler falls back to 404 for the authenticated retry.

        var resolver = CreateResolver(handler);
        var digest = await resolver.ResolveAsync("ghost:4.0.0");

        await Assert.That(digest).IsNull();
    }

    private static OciImageDigestResolver CreateResolver(
        StubHttpMessageHandler handler,
        FixImagesConfig? config = null,
        string? dockerConfigPath = null)
    {
        // Use a non-existent path by default to isolate tests from the host's Docker configuration.
        // CI runners (e.g. GitHub Actions) may have Docker Hub credentials in ~/.docker/config.json,
        // which would bypass the bearer token challenge flow and cause test failures.
        dockerConfigPath ??= Path.Combine(Path.GetTempPath(), "__nonexistent_seiton_test_docker_config__.json");
        return new OciImageDigestResolver(new HttpClient(handler), config ?? new FixImagesConfig(), dockerConfigPath);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _handlers = [];

        public List<string> RequestedUris { get; } = [];
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }

        public void AddHead(string uri, string digest, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _handlers.Add((
                req => req.Method == HttpMethod.Head
                    && req.RequestUri!.ToString() == uri,
                _ =>
                {
                    var response = new HttpResponseMessage(statusCode);
                    response.Headers.Add("Docker-Content-Digest", digest);
                    return response;
                }
            ));
        }

        // Adds a handler that returns 401 + WWW-Authenticate: Bearer for anonymous HEAD requests.
        public void AddBearerChallenge(string headUri, string realm, string? service, string? scope)
        {
            var wwwAuthenticate = BuildWwwAuthHeader(realm, service, scope);
            _handlers.Add((
                req => req.Method == HttpMethod.Head
                    && req.RequestUri!.ToString() == headUri
                    && req.Headers.Authorization is null,
                _ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    response.Headers.TryAddWithoutValidation("WWW-Authenticate", wwwAuthenticate);
                    return response;
                }
            ));
        }

        // Adds a handler for the OCI token endpoint that returns an access_token JSON payload.
        public void AddTokenEndpoint(string tokenBaseUri, string token)
        {
            _handlers.Add((
                req => req.Method == HttpMethod.Get
                    && req.RequestUri!.ToString().StartsWith(tokenBaseUri, StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{{\"access_token\":\"{token}\",\"token\":\"{token}\"}}",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                }));
        }

        // Adds a handler for a HEAD request authenticated with a specific Bearer token.
        public void AddHeadWithBearer(string uri, string bearerToken, string digest)
        {
            _handlers.Add((
                req => req.Method == HttpMethod.Head
                    && req.RequestUri!.ToString() == uri
                    && req.Headers.Authorization?.Scheme == "Bearer"
                    && req.Headers.Authorization?.Parameter == bearerToken,
                _ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK);
                    response.Headers.Add("Docker-Content-Digest", digest);
                    return response;
                }
            ));
        }

        private static string BuildWwwAuthHeader(string realm, string? service, string? scope)
        {
            var sb = new System.Text.StringBuilder("Bearer realm=\"").Append(realm).Append('"');
            if (service is not null)
            {
                sb.Append(",service=\"").Append(service).Append('"');
            }

            if (scope is not null)
            {
                sb.Append(",scope=\"").Append(scope).Append('"');
            }

            return sb.ToString();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            RequestedUris.Add(uri);
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;

            foreach (var (match, respond) in _handlers)
            {
                if (match(request))
                {
                    return Task.FromResult(respond(request));
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
