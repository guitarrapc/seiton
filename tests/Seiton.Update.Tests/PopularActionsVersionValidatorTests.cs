using System.Net;
using Seiton.Update.Validators;

namespace Seiton.Update.Tests;

public sealed class PopularActionsVersionValidatorTests
{
    [Test]
    public async Task ValidateAsync_WithFakeGitHubTags_ReturnsStaleTargets()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "seiton-popular-actions-version-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(repoRoot, "data", "sources", "popular-actions"));
            File.WriteAllText(
                Path.Combine(repoRoot, "data", "sources", "popular-actions", "targets.json"),
                """
                {
                  "schemaVersion": 1,
                  "targets": [
                    { "actionRef": "actions/cache@v5" },
                    { "actionRef": "actions/checkout@v6" },
                                        { "actionRef": "octokit/request-action@v3.0.0" },
                                        { "actionRef": "pypa/gh-action-pypi-publish@release/v1" }
                  ]
                }
                """.Replace("\r\n", "\n"));

            var handler = new FakeGitHubTagsHandler
            {
                Responses =
                {
                    ["/repos/actions/cache/tags"] = """
                        [{ "name": "v5" }, { "name": "v4" }]
                        """,
                    ["/repos/actions/checkout/tags"] = """
                        [{ "name": "v7" }, { "name": "v6" }, { "name": "v6.1.0" }]
                        """,
                    ["/repos/octokit/request-action/tags"] = """
                        [{ "name": "v4.0.0" }, { "name": "v3.0.0" }]
                        """,
                    ["/repos/pypa/gh-action-pypi-publish/tags"] = """
                        [{ "name": "release/v2" }, { "name": "release/v1" }]
                        """,
                },
            };
            var validator = new PopularActionsVersionValidator(() => new HttpClient(handler, disposeHandler: false));

            var result = await validator.ValidateAsync(repoRoot);

            await Assert.That(result.StaleVersions).Count().IsEqualTo(3);
            await Assert.That(result.UnresolvedVersions).Count().IsEqualTo(0);
            await Assert.That(result.StaleVersions[0].ActionRef).IsEqualTo("actions/checkout@v6");
            await Assert.That(result.StaleVersions[0].CurrentMajor).IsEqualTo(6);
            await Assert.That(result.StaleVersions[0].LatestMajor).IsEqualTo(7);
            await Assert.That(result.StaleVersions[1].ActionRef).IsEqualTo("octokit/request-action@v3.0.0");
            await Assert.That(result.StaleVersions[1].CurrentMajor).IsEqualTo(3);
            await Assert.That(result.StaleVersions[1].LatestMajor).IsEqualTo(4);
            await Assert.That(result.StaleVersions[2].ActionRef).IsEqualTo("pypa/gh-action-pypi-publish@release/v1");
            await Assert.That(result.StaleVersions[2].CurrentMajor).IsEqualTo(1);
            await Assert.That(result.StaleVersions[2].LatestMajor).IsEqualTo(2);
            await Assert.That(handler.RequestPaths).Contains("/repos/actions/cache/tags");
            await Assert.That(handler.RequestPaths).Contains("/repos/actions/checkout/tags");
            await Assert.That(handler.RequestPaths).Contains("/repos/octokit/request-action/tags");
            await Assert.That(handler.RequestPaths).Contains("/repos/pypa/gh-action-pypi-publish/tags");
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task ValidateAsync_CurrentTargetsAgainstGitHubTags_HasNoStaleTargets()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_TOKEN")))
        {
            Skip.Test("GITHUB_TOKEN is required for live GitHub tag validation to avoid unauthenticated API rate limits.");
        }

        var repoRoot = FindRepoRoot();
        var validator = new PopularActionsVersionValidator();

        var result = await validator.ValidateAsync(repoRoot);

        await Assert.That(result.UnresolvedVersions).Count().IsEqualTo(0);
        await Assert.That(result.StaleVersions).Count().IsEqualTo(0);
    }

    private sealed class FakeGitHubTagsHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Responses { get; } = new(StringComparer.Ordinal);
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            RequestPaths.Add(path);

            if (!Responses.TryGetValue(path, out var json))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var slnxPath = Path.Combine(dir.FullName, "seiton.slnx");
            if (File.Exists(slnxPath))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found from test base directory.");
    }
}
