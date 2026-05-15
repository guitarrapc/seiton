using System.Reflection;
using Seiton.Core.Linting;

namespace Seiton.Core.Tests;

public sealed class LocalActionOutputResolverUnitTests
{
    [Test]
    public async Task ResolveOutputNames_SlashNormalizedGithubActionPath_UsesCachedValue()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");
        var resolver = new LocalActionOutputResolver(workflowPath);
        var cached = new[] { "cached_output" };

        var cacheField = typeof(LocalActionOutputResolver).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);
        await Assert.That(cacheField).IsNotNull();

        var cache = cacheField!.GetValue(resolver) as Dictionary<string, string[]?>;
        await Assert.That(cache).IsNotNull();

        var normalizedKey = ActionRefHelpers.NormalizePath(Path.GetFullPath(Path.Combine(repositoryRoot, ".github", "actions", "sample")));
        cache![normalizedKey] = cached;

        var resolved = resolver.ResolveOutputNames("./.github/actions/sample"u8);

        await Assert.That(ReferenceEquals(resolved, cached)).IsTrue();
    }

    [Test]
    public async Task ResolveOutputNames_PathTraversal_ReturnsNull()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");
        var resolver = new LocalActionOutputResolver(workflowPath);

        // Attempt to escape the repository root
        var result = resolver.ResolveOutputNames("./../../../../../../etc/passwd"u8);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ResolveOutputNames_RelativeParentWithinRepo_NotBlockedByTraversalCheck()
    {
        // ../sibling paths resolve within the repo and should NOT be blocked
        // (they will return null because the action.yml doesn't exist, not because of traversal)
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workflowPath = Path.Combine(repositoryRoot, ".github", "workflows", "caller.yml");
        var resolver = new LocalActionOutputResolver(workflowPath);

        // ../actions/foo resolves to .github/actions/foo which is within the repo
        // Since no file exists, it returns null - but should NOT be blocked by traversal check
        var result = resolver.ResolveOutputNames("../actions/foo"u8);

        // It returns null because the directory/file doesn't exist, not because of traversal
        await Assert.That(result).IsNull();
    }
}
