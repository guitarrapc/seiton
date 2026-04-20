using System.Net;
using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.OnlineAudit;

namespace Seiton.Core.Tests;

public sealed class ActionRefResolverTests
{
    [Test]
    public async Task ResolveAsync_ReturnsBranchAndTagPresence_ForSymbolicRef()
    {
        var handler = new StubHttpMessageHandler();
        handler.AddJson(
            "https://api.github.com/repos/actions/setup-go/git/ref/heads/main",
            """
            { "ref": "refs/heads/main" }
            """);
        handler.AddJson(
            "https://api.github.com/repos/actions/setup-go/git/ref/tags/main",
            """
            { "ref": "refs/tags/main" }
            """);

        var resolver = CreateResolver(handler);

        var result = await resolver.ResolveAsync("actions", "setup-go", "main");

        await Assert.That(result.HasBranchReference).IsTrue();
        await Assert.That(result.HasTagReference).IsTrue();
    }

    [Test]
    public async Task ResolveAsync_ReturnsCommitReachabilityAndTaggedState_ForPinnedSha()
    {
        var sha = "0123456789abcdef0123456789abcdef01234567";
        var handler = new StubHttpMessageHandler();
        handler.AddJson(
            $"https://api.github.com/repos/actions/checkout/commits/{sha}",
            $$"""
            { "sha": "{{sha}}" }
            """
        );
        handler.AddJson(
            "https://api.github.com/repos/actions/checkout/tags?per_page=100",
            $$"""
            [
              {
                "name": "v4",
                "commit": {
                  "sha": "{{sha}}"
                }
              }
            ]
            """
        );

        var resolver = CreateResolver(handler);

        var result = await resolver.ResolveAsync("actions", "checkout", sha);

        await Assert.That(result.CommitExists).IsTrue();
        await Assert.That(result.IsTaggedCommit).IsTrue();
    }

    [Test]
    public async Task ResolveAsync_FallsBackToGitHubCom_WhenGhesReturns404_AndFallbackEnabled()
    {
        var handler = new StubHttpMessageHandler();
        handler.AddStatus("https://ghes.example.com/api/v3/repos/actions/setup-go/git/ref/heads/release", HttpStatusCode.NotFound);
        handler.AddJson(
            "https://api.github.com/repos/actions/setup-go/git/ref/heads/release",
            """
            { "ref": "refs/heads/release" }
            """);
        handler.AddStatus("https://ghes.example.com/api/v3/repos/actions/setup-go/git/ref/tags/release", HttpStatusCode.NotFound);
        handler.AddStatus("https://api.github.com/repos/actions/setup-go/git/ref/tags/release", HttpStatusCode.NotFound);

        var resolver = CreateResolver(handler, new GitHubNetworkConfig
        {
            GhesApiUrl = "https://ghes.example.com/api/v3",
            GhesFallback = true,
        });

        var result = await resolver.ResolveAsync("actions", "setup-go", "release");

        await Assert.That(result.HasBranchReference).IsTrue();
        await Assert.That(handler.RequestedUris).Contains("https://ghes.example.com/api/v3/repos/actions/setup-go/git/ref/heads/release");
        await Assert.That(handler.RequestedUris).Contains("https://api.github.com/repos/actions/setup-go/git/ref/heads/release");
    }

    [Test]
    public async Task ResolveAsync_UsesCache_ForRepeatedRequests()
    {
        var handler = new StubHttpMessageHandler();
        handler.AddJson(
            "https://api.github.com/repos/actions/setup-go/git/ref/heads/main",
            """
            { "ref": "refs/heads/main" }
            """);
        handler.AddStatus("https://api.github.com/repos/actions/setup-go/git/ref/tags/main", HttpStatusCode.NotFound);

        var resolver = CreateResolver(handler);

        _ = await resolver.ResolveAsync("actions", "setup-go", "main");
        _ = await resolver.ResolveAsync("actions", "setup-go", "main");

        await Assert.That(handler.RequestedUris.Count(uri => uri == "https://api.github.com/repos/actions/setup-go/git/ref/heads/main")).IsEqualTo(1);
    }

    static ActionRefResolver CreateResolver(
        StubHttpMessageHandler handler,
        GitHubNetworkConfig? config = null)
    {
        return new ActionRefResolver(new HttpClient(handler), config ?? new GitHubNetworkConfig());
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        readonly Dictionary<string, HttpResponseMessage> responses = new(StringComparer.Ordinal);

        public List<string> RequestedUris { get; } = [];

        public void AddJson(string uri, string json)
        {
            responses[uri] = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        public void AddStatus(string uri, HttpStatusCode statusCode)
        {
            responses[uri] = new HttpResponseMessage(statusCode);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            RequestedUris.Add(uri);
            if (responses.TryGetValue(uri, out var response))
            {
                return Task.FromResult(CloneResponse(response));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        static HttpResponseMessage CloneResponse(HttpResponseMessage response)
        {
            var clone = new HttpResponseMessage(response.StatusCode);
            if (response.Content is not null)
            {
                var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                clone.Content = new StringContent(content, Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "application/json");
            }

            return clone;
        }
    }
}
