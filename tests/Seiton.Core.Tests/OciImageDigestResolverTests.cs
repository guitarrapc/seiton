using System.Net;
using Seiton.Core.Linting;
using Seiton.Core.Linting.PinRemediation;

namespace Seiton.Core.Tests;

public sealed class OciImageDigestResolverTests
{
    const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

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

    static OciImageDigestResolver CreateResolver(
        StubHttpMessageHandler handler,
        FixImagesConfig? config = null,
        string? dockerConfigPath = null)
    {
        var client = new HttpClient(handler);
        var factory = new StubHttpClientFactory(client);
        return new OciImageDigestResolver(factory, config ?? new FixImagesConfig(), dockerConfigPath);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.Ordinal);

        public List<string> RequestedUris { get; } = [];
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }

        public void AddHead(string uri, string digest, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responses[uri] = () =>
            {
                var response = new HttpResponseMessage(statusCode);
                response.Headers.Add("Docker-Content-Digest", digest);
                return response;
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            RequestedUris.Add(uri);
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;

            if (request.Method != HttpMethod.Head)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            }

            if (_responses.TryGetValue(uri, out var responseFactory))
            {
                return Task.FromResult(responseFactory());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
