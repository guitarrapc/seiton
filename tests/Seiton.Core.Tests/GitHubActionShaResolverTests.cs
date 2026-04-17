using System.Net;
using System.Net.Http;
using System.Text;
using Seiton.Core.Linting.PinRemediation;

namespace Seiton.Core.Tests;

public sealed class GitHubActionShaResolverTests
{
    [Test]
    public async Task ResolveAsync_ReturnsCommitSha_ForDirectTagReference()
    {
        var handler = new StubHttpMessageHandler();
        handler.AddJson(
            "https://api.github.com/repos/actions/checkout/git/ref/tags/v4",
            """
            {
              "object": {
                "type": "commit",
                "sha": "0123456789abcdef0123456789abcdef01234567"
              }
            }
            """);

        var resolver = CreateResolver(handler);

        var (sha, tagComment) = await resolver.ResolveAsync("actions", "checkout", "v4");

        await Assert.That(sha).IsEqualTo("0123456789abcdef0123456789abcdef01234567");
        await Assert.That(tagComment).IsEqualTo("v4");
    }

    [Test]
    public async Task ResolveAsync_FollowsAnnotatedTag_ToCommitSha()
    {
        var handler = new StubHttpMessageHandler();
        handler.AddJson(
            "https://api.github.com/repos/actions/cache/git/ref/tags/v3.3.1",
            """
            {
              "object": {
                "type": "tag",
                "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
              }
            }
            """);
        handler.AddJson(
            "https://api.github.com/repos/actions/cache/git/tags/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            """
            {
              "object": {
                "type": "commit",
                "sha": "fedcba9876543210fedcba9876543210fedcba98"
              }
            }
            """);

        var resolver = CreateResolver(handler);

        var (sha, tagComment) = await resolver.ResolveAsync("actions", "cache", "v3.3.1");

        await Assert.That(sha).IsEqualTo("fedcba9876543210fedcba9876543210fedcba98");
        await Assert.That(tagComment).IsEqualTo("v3.3.1");
    }

    [Test]
    public async Task ResolveAsync_FallsBackToGitHubCom_WhenGhesReturns404_AndFallbackEnabled()
    {
        var handler = new StubHttpMessageHandler();
        handler.AddStatus("https://ghes.example.com/api/v3/repos/actions/setup-go/git/ref/tags/v5", HttpStatusCode.NotFound);
        handler.AddJson(
            "https://api.github.com/repos/actions/setup-go/git/ref/tags/v5",
            """
            {
              "object": {
                "type": "commit",
                "sha": "1111111111111111111111111111111111111111"
              }
            }
            """);

        var resolver = CreateResolver(
            handler,
            new GitHubActionsResolutionConfig
            {
                GhesApiUrl = "https://ghes.example.com/api/v3",
                GhesFallback = true,
            });

        var (sha, _) = await resolver.ResolveAsync("actions", "setup-go", "v5");

        await Assert.That(sha).IsEqualTo("1111111111111111111111111111111111111111");
        await Assert.That(handler.RequestedUris).Contains("https://ghes.example.com/api/v3/repos/actions/setup-go/git/ref/tags/v5");
        await Assert.That(handler.RequestedUris).Contains("https://api.github.com/repos/actions/setup-go/git/ref/tags/v5");
    }

    [Test]
    public async Task ResolveAsync_ReturnsNull_WhenReferenceMatchesSkipRules()
    {
        var handler = new StubHttpMessageHandler();
        var resolver = CreateResolver(
            handler,
            new GitHubActionsResolutionConfig
            {
                ExcludeBranches = ["main"],
                IgnoreActions = [new IgnoreActionEntry("actions/checkout", ".*")],
            });

        var skippedBranch = await resolver.ResolveAsync("actions", "checkout", "main");
        var skippedAction = await resolver.ResolveAsync("actions", "checkout", "v4");

        await Assert.That(skippedBranch.Sha).IsNull();
        await Assert.That(skippedBranch.TagComment).IsNull();
        await Assert.That(skippedAction.Sha).IsNull();
        await Assert.That(skippedAction.TagComment).IsNull();
        await Assert.That(handler.RequestedUris).IsEmpty();
    }

    [Test]
    public async Task ResolveAsync_ReturnsNull_WhenTagIsTooNew_ForLightweightTag()
    {
        var recentDate = DateTimeOffset.UtcNow.AddDays(-1).ToString("o");
        var handler = new StubHttpMessageHandler();
        handler.AddJson(
            "https://api.github.com/repos/actions/checkout/git/ref/tags/v4",
            $$"""
            {
              "object": {
                "type": "commit",
                "sha": "0123456789abcdef0123456789abcdef01234567"
              }
            }
            """);
        handler.AddJson(
            "https://api.github.com/repos/actions/checkout/commits/0123456789abcdef0123456789abcdef01234567",
            $$"""
            {
              "sha": "0123456789abcdef0123456789abcdef01234567",
              "commit": {
                "committer": {
                  "date": "{{recentDate}}"
                }
              }
            }
            """);

        var resolver = CreateResolver(handler, new GitHubActionsResolutionConfig { MinAgeDays = 14 });
        var (sha, tagComment) = await resolver.ResolveAsync("actions", "checkout", "v4");

        await Assert.That(sha).IsNull();
        await Assert.That(tagComment).IsNull();
    }

    [Test]
    public async Task ResolveAsync_ReturnsNull_WhenTagIsTooNew_ForAnnotatedTag()
    {
        var recentDate = DateTimeOffset.UtcNow.AddDays(-1).ToString("o");
        var handler = new StubHttpMessageHandler();
        handler.AddJson(
            "https://api.github.com/repos/actions/cache/git/ref/tags/v3.3.1",
            """
            {
              "object": {
                "type": "tag",
                "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
              }
            }
            """);
        handler.AddJson(
            "https://api.github.com/repos/actions/cache/git/tags/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            $$"""
            {
              "tagger": {
                "date": "{{recentDate}}"
              },
              "object": {
                "type": "commit",
                "sha": "fedcba9876543210fedcba9876543210fedcba98"
              }
            }
            """);

        var resolver = CreateResolver(handler, new GitHubActionsResolutionConfig { MinAgeDays = 14 });
        var (sha, tagComment) = await resolver.ResolveAsync("actions", "cache", "v3.3.1");

        await Assert.That(sha).IsNull();
        await Assert.That(tagComment).IsNull();
    }

    [Test]
    public async Task ResolveAsync_ReturnsSha_WhenTagIsOldEnough()
    {
        var oldDate = DateTimeOffset.UtcNow.AddDays(-30).ToString("o");
        var handler = new StubHttpMessageHandler();
        handler.AddJson(
            "https://api.github.com/repos/actions/checkout/git/ref/tags/v4",
            """
            {
              "object": {
                "type": "commit",
                "sha": "0123456789abcdef0123456789abcdef01234567"
              }
            }
            """);
        handler.AddJson(
            "https://api.github.com/repos/actions/checkout/commits/0123456789abcdef0123456789abcdef01234567",
            $$"""
            {
              "sha": "0123456789abcdef0123456789abcdef01234567",
              "commit": {
                "committer": {
                  "date": "{{oldDate}}"
                }
              }
            }
            """);

        var resolver = CreateResolver(handler, new GitHubActionsResolutionConfig { MinAgeDays = 14 });
        var (sha, tagComment) = await resolver.ResolveAsync("actions", "checkout", "v4");

        await Assert.That(sha).IsEqualTo("0123456789abcdef0123456789abcdef01234567");
        await Assert.That(tagComment).IsEqualTo("v4");
    }

    [Test]
    public async Task ResolveAsync_ReturnsSha_WhenMinAgeDaysIsZero_DisablesAgeCheck()
    {
        var recentDate = DateTimeOffset.UtcNow.AddDays(-1).ToString("o");
        var handler = new StubHttpMessageHandler();
        handler.AddJson(
            "https://api.github.com/repos/actions/checkout/git/ref/tags/v4",
            """
            {
              "object": {
                "type": "commit",
                "sha": "0123456789abcdef0123456789abcdef01234567"
              }
            }
            """);
        handler.AddJson(
            "https://api.github.com/repos/actions/checkout/commits/0123456789abcdef0123456789abcdef01234567",
            $$"""
            {
              "sha": "0123456789abcdef0123456789abcdef01234567",
              "commit": {
                "committer": {
                  "date": "{{recentDate}}"
                }
              }
            }
            """);

        var resolver = CreateResolver(handler, new GitHubActionsResolutionConfig { MinAgeDays = 0 });
        var (sha, tagComment) = await resolver.ResolveAsync("actions", "checkout", "v4");

        await Assert.That(sha).IsEqualTo("0123456789abcdef0123456789abcdef01234567");
        await Assert.That(tagComment).IsEqualTo("v4");
    }

    [Test]
    public async Task ResolveAsync_CachesSuccessfulResolution()
    {
        var handler = new StubHttpMessageHandler();
        handler.AddJson(
            "https://api.github.com/repos/actions/checkout/git/ref/tags/v4",
            """
            {
              "object": {
                "type": "commit",
                "sha": "0123456789abcdef0123456789abcdef01234567"
              }
            }
            """);

        var resolver = CreateResolver(handler);

        var first = await resolver.ResolveAsync("actions", "checkout", "v4");
        var second = await resolver.ResolveAsync("actions", "checkout", "v4");

        await Assert.That(first.Sha).IsEqualTo(second.Sha);
        await Assert.That(handler.RequestedUris.Count(uri => uri == "https://api.github.com/repos/actions/checkout/git/ref/tags/v4"))
            .IsEqualTo(1);
    }

    static GitHubActionShaResolver CreateResolver(
        StubHttpMessageHandler handler,
        GitHubActionsResolutionConfig? config = null)
    {
        var client = new HttpClient(handler);
        var factory = new StubHttpClientFactory(client);
        return new GitHubActionShaResolver(factory, config ?? new GitHubActionsResolutionConfig());
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.Ordinal);

        public List<string> RequestedUris { get; } = [];

        public void AddJson(string uri, string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responses[uri] = () => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        public void AddStatus(string uri, HttpStatusCode statusCode)
        {
            _responses[uri] = () => new HttpResponseMessage(statusCode);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            RequestedUris.Add(uri);
            if (_responses.TryGetValue(uri, out var responseFactory))
            {
                return Task.FromResult(responseFactory());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
